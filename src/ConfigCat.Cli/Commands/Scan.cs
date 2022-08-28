﻿using ConfigCat.Cli.Models.Api;
using ConfigCat.Cli.Models.Scan;
using ConfigCat.Cli.Services;
using ConfigCat.Cli.Services.Api;
using ConfigCat.Cli.Services.Exceptions;
using ConfigCat.Cli.Services.FileSystem;
using ConfigCat.Cli.Services.Git;
using ConfigCat.Cli.Services.Rendering;
using ConfigCat.Cli.Services.Scan;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace ConfigCat.Cli.Commands;

internal class Scan
{
    private static readonly Lazy<string> Version = new(() =>
        Assembly.GetEntryAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            .InformationalVersion);

    private readonly IWorkspaceLoader workspaceLoader;
    private readonly IFlagClient flagClient;
    private readonly ICodeReferenceClient codeReferenceClient;
    private readonly IFileCollector fileCollector;
    private readonly IFileScanner fileScanner;
    private readonly IGitClient gitClient;
    private readonly IOutput output;

    public Scan(IWorkspaceLoader workspaceLoader,
        IFlagClient flagClient,
        ICodeReferenceClient codeReferenceClient,
        IFileCollector fileCollector,
        IFileScanner fileScanner,
        IGitClient gitClient,
        IOutput output)
    {
        this.workspaceLoader = workspaceLoader;
        this.flagClient = flagClient;
        this.codeReferenceClient = codeReferenceClient;
        this.fileCollector = fileCollector;
        this.fileScanner = fileScanner;
        this.gitClient = gitClient;
        this.output = output;
    }

    public async Task<int> InvokeAsync(DirectoryInfo directory,
        string configId,
        int lineCount,
        bool print,
        bool upload,
        string repo,
        string branch,
        string commitHash,
        string fileUrlTemplate,
        string commitUrlTemplate,
        string runner,
        CancellationToken token)
    {
        if (upload && repo.IsEmpty())
            throw new ShowHelpException("The --repo argument is required for code reference upload.");

        if (configId.IsEmpty())
        {
            this.output.WriteLine("Comparing the feature flags in the code to the feature flags in the ConfigCat Dashboard.");
            configId = (await this.workspaceLoader.LoadConfigAsync(token)).ConfigId;
        }

        lineCount = lineCount is < 0 or > 10
            ? 4
            : lineCount;

        var flags = await this.flagClient.GetFlagsAsync(configId, token);
        var deletedFlags = await this.flagClient.GetDeletedFlagsAsync(configId, token);
        deletedFlags = deletedFlags
            .Where(d => flags.All(f => f.Key != d.Key))
            .Distinct(new FlagModelEqualityComparer());

        var files = await this.fileCollector.CollectAsync(directory, token);
        var flagReferences = await this.fileScanner.ScanAsync(flags.Concat(deletedFlags).ToArray(), files.ToArray(), lineCount, token);

        var flagReferenceResults = flagReferences as FlagReferenceResult[] ?? flagReferences.ToArray();
        var aliveFlagReferences = Filter(flagReferenceResults, r => r.FoundFlag is not DeletedFlagModel).ToArray();
        var deletedFlagReferences = Filter(flagReferenceResults, r => r.FoundFlag is DeletedFlagModel).ToArray();

        this.output.Write("Found ")
            .WriteCyan(aliveFlagReferences.Sum(f => f.References.Count).ToString())
            .Write($" feature flag / setting reference(s) in ")
            .WriteCyan(aliveFlagReferences.Length.ToString())
            .Write(" file(s). " +
                   $"Keys: [{string.Join(", ", aliveFlagReferences.SelectMany(r => r.References).Select(r => r.FoundFlag.Key).Distinct())}]")
            .WriteLine();

        if (print)
            this.PrintReferences(aliveFlagReferences, token);

        if (deletedFlagReferences.Length > 0)
            this.output.WriteWarning(
                $"{deletedFlagReferences.Sum(f => f.References.Count)} deleted feature flag/setting " +
                $"reference(s) found in {deletedFlagReferences.Length} file(s). " +
                $"Keys: [{string.Join(", ", deletedFlagReferences.SelectMany(r => r.References).Select(r => r.FoundFlag.Key).Distinct())}]");
        else
            this.output.WriteGreen("OK. Didn't find any deleted feature flag / setting references.");

        this.output.WriteLine();

        if (print)
            this.PrintReferences(deletedFlagReferences, token);

        if (!upload) return ExitCodes.Ok;

        this.output.WriteLine("Initiating code reference upload...");

        var gitInfo = this.gitClient.GatherGitInfo(directory.FullName);

        branch ??= gitInfo?.Branch;
        commitHash ??= gitInfo?.CurrentCommitHash;

        if (branch.IsEmpty())
            throw new ShowHelpException(
                "Could not determine the current branch name, make sure the scanned folder is inside a Git repository, or use the --branch argument.");

        this.output.Write("Repository").Write(":").WriteCyan($" {repo}").WriteLine()
            .Write("Branch").Write(":").WriteCyan($" {branch}").WriteLine()
            .Write("Commit").Write(":").WriteCyan($" {commitHash}").WriteLine();

        var repositoryDirectory = gitInfo == null || gitInfo.WorkingDirectory.IsEmpty()
            ? directory.FullName.AsSlash()
            : gitInfo.WorkingDirectory;
        await this.codeReferenceClient.UploadAsync(new CodeReferenceRequest
        {
            FlagReferences = aliveFlagReferences
                .SelectMany(referenceResult => referenceResult.References, (file, reference) => new { file.File, reference })
                .GroupBy(r => r.reference.FoundFlag)
                .Select(r => new FlagReference
                {
                    SettingId = r.Key.SettingId,
                    References = r.Select(item => new ReferenceLines
                    {
                        File = item.File.FullName.AsSlash().Replace(repositoryDirectory, string.Empty, StringComparison.OrdinalIgnoreCase).Trim('/'),
                        FileUrl = !commitHash.IsEmpty() && !fileUrlTemplate.IsEmpty()
                            ? fileUrlTemplate
                                .Replace("{commitHash}", commitHash)
                                .Replace("{filePath}", item.File.FullName.AsSlash().Replace(repositoryDirectory, string.Empty, StringComparison.OrdinalIgnoreCase).Trim('/'))
                                .Replace("{lineNumber}", item.reference.ReferenceLine.LineNumber.ToString())
                            : null,
                        PostLines = item.reference.PostLines,
                        PreLines = item.reference.PreLines,
                        ReferenceLine = item.reference.ReferenceLine
                    }).ToList()
                }).ToList(),
            Repository = repo,
            Branch = branch,
            CommitHash = commitHash,
            CommitUrl = !commitHash.IsEmpty() && !commitUrlTemplate.IsEmpty()
                ? commitUrlTemplate.Replace("{commitHash}", commitHash)
                : null,
            ActiveBranches = gitInfo?.ActiveBranches,
            ConfigId = configId,
            Uploader = runner ?? $"ConfigCat CLI {Version.Value}",
        }, token);


        return ExitCodes.Ok;
    }

    private void PrintReferences(FlagReferenceResult[] references, CancellationToken token)
    {
        if (references.Length == 0)
            return;

        this.output.WriteLine();
        foreach (var fileReference in references)
        {
            if (token.IsCancellationRequested)
                break;

            this.output.WriteYellow(fileReference.File.FullName).WriteLine();
            foreach (var reference in fileReference.References)
            {
                if (token.IsCancellationRequested)
                    break;

                var maxDigitCount = reference.PostLines.Count > 0
                    ? reference.PostLines.Max(pl => pl.LineNumber).GetDigitCount()
                    : reference.ReferenceLine.LineNumber.GetDigitCount();
                foreach (var preLine in reference.PreLines)
                    this.PrintRegularLine(preLine, maxDigitCount);

                this.PrintSelectedLine(reference.ReferenceLine, maxDigitCount, reference);

                foreach (var postLine in reference.PostLines)
                    this.PrintRegularLine(postLine, maxDigitCount);

                this.output.WriteLine();
            }
        }
    }

    private void PrintRegularLine(Line line, int maxDigitCount)
    {
        var spaces = maxDigitCount - line.LineNumber.GetDigitCount();
        this.output.WriteCyan($"{line.LineNumber}:")
            .Write($"{new string(' ', spaces)} ")
            .WriteDarkGray(line.LineText)
            .WriteLine();
    }

    private void PrintSelectedLine(Line line, int maxDigitCount, Reference reference)
    {
        var spaces = maxDigitCount - line.LineNumber.GetDigitCount();
        this.output.WriteCyan($"{line.LineNumber}:")
            .Write($"{new string(' ', spaces)} ");

        this.SearchKeyInText(line.LineText, reference);

        this.output.WriteLine();
    }

    private void SearchKeyInText(string text, Reference reference)
    {
        var keyIndex = text.IndexOf(reference.FoundFlag.Key, StringComparison.Ordinal);
        var key = reference.FoundFlag.Key;
        if (keyIndex == -1)
        {
            if (reference.FoundFlag.Aliases != null)
            {
                foreach (var alias in reference.FoundFlag.Aliases)
                {
                    keyIndex = text.IndexOf(alias, StringComparison.Ordinal);
                    key = alias;
                    if (keyIndex != -1)
                        break;
                }
            }

            if (keyIndex == -1)
            {
                if (reference.MatchedSample != null)
                {
                    keyIndex = text.IndexOf(reference.MatchedSample, StringComparison.Ordinal);
                    key = reference.MatchedSample;
                }

                if (keyIndex == -1)
                {
                    this.output.Write(text);
                    return;
                }
            }
        }

        var preText = text[..keyIndex];
        var postText = text[(keyIndex + key.Length)..text.Length];
        this.output.Write(preText)
            .WriteColor(key, ConsoleColor.White, ConsoleColor.DarkMagenta);
        this.SearchKeyInText(postText, reference);
    }

    private static IEnumerable<FlagReferenceResult> Filter(IEnumerable<FlagReferenceResult> source,
        Predicate<Reference> filter)
    {
        foreach (var item in source)
        {
            var references = FilterReference(item.References, filter);
            if (!references.Any())
                continue;

            yield return item;
        }

        IEnumerable<Reference> FilterReference(IEnumerable<Reference> references, Predicate<Reference> predicate)
        {
            foreach (var item in references)
            {
                if (!predicate(item))
                    continue;

                yield return item;
            }
        }
    }
}

class FlagModelEqualityComparer : IEqualityComparer<DeletedFlagModel>
{
    public bool Equals([AllowNull] DeletedFlagModel x, [AllowNull] DeletedFlagModel y)
    {
        if (x is null || y is null)
            return false;

        return x.Key == y.Key;
    }

    public int GetHashCode([DisallowNull] DeletedFlagModel obj)
    {
        return obj.Key.GetHashCode();
    }
}