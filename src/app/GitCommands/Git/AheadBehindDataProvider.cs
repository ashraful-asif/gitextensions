﻿using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using GitExtensions.Extensibility;
using GitExtUtils;

namespace GitCommands.Git
{
    public interface IAheadBehindDataProvider
    {
        IDictionary<string, AheadBehindData>? GetData(string branchName = "");
        void ResetCache();
    }

    public partial class AheadBehindDataProvider : IAheadBehindDataProvider
    {
        private readonly Func<IExecutable> _getGitExecutable;

        // Parse info about remote branches, see below for explanation
        // This assumes that the Git output is not localised
        [GeneratedRegex(@"^((?<gone_p>gone)|((ahead\s(?<ahead_p>\d+))?(,\s)?(behind\s(?<behind_p>\d+))?)|(?<unk_p>.*?))::
                   ((?<gone_u>gone)|((ahead\s(?<ahead_u>\d+))?(,\s)?(behind\s(?<behind_u>\d+))?)|(?<unk_u>.*?))::
                   (?<remote_p>.*?)::(?<remote_u>.*?)::(?<branch>.*)$",
                RegexOptions.Multiline | RegexOptions.IgnorePatternWhitespace | RegexOptions.ExplicitCapture)]
        private static partial Regex AheadBehindRegex();
        private readonly string _refFormat = @"%(push:track,nobracket)::%(upstream:track,nobracket)::%(push)::%(upstream)::%(refname:short)";
        private Lazy<IDictionary<string, AheadBehindData>?> _lazyData;
        private string _branchName;
        private object _lock = new();

        public AheadBehindDataProvider(Func<IExecutable> getGitExecutable)
        {
            _getGitExecutable = getGitExecutable;
        }

        public void ResetCache()
        {
            _lazyData = null;
            _branchName = null;
        }

        public IDictionary<string, AheadBehindData>? GetData(string branchName = "")
        {
            if (!AppSettings.ShowAheadBehindData)
            {
                return null;
            }

            lock (_lock)
            {
                // Callers setting branch name has the responsibility to ensure that not all are needed
                if (string.IsNullOrWhiteSpace(branchName) && !string.IsNullOrWhiteSpace(_branchName))
                {
                    Debug.WriteLine($"Call for all branches after cache filled with specific branch {_branchName}");
                    ResetCache();
                }

                // Use Lazy<> to synchronize callers
                if (_lazyData == null)
                {
                    _lazyData = new(() => GetData(null, branchName));
                    _branchName = branchName;
                }

                return _lazyData.Value;
            }
        }

        // This method is required to facilitate unit tests
        private IDictionary<string, AheadBehindData>? GetData(Encoding? encoding, string branchName = "")
        {
            if (branchName is null)
            {
                throw new ArgumentException(nameof(branchName));
            }

            if (branchName == DetachedHeadParser.DetachedBranch)
            {
                return null;
            }

            GitArgumentBuilder aheadBehindGitCommand = new("for-each-ref")
            {
                $"--format=\"{_refFormat}\"",
                "refs/heads/" + branchName
            };

            ExecutionResult result = GetGitExecutable().Execute(aheadBehindGitCommand, outputEncoding: encoding, throwOnErrorExit: false);
            if (!result.ExitedSuccessfully || string.IsNullOrEmpty(result.StandardOutput))
            {
                return null;
            }

            MatchCollection matches = AheadBehindRegex().Matches(result.StandardOutput);
            Dictionary<string, AheadBehindData> aheadBehindForBranchesData = [];
            foreach (Match match in matches)
            {
                string branch = match.Groups["branch"].Value;

                // Use 'remote_p' (the value of '%(push)') if all of the following conditions are met:
                // 1. The value exists
                // 2. The value is not empty
                // 3. The value of '%(push:track,nobracket)' is not 'gone'
                //
                // The third condition specifically ensures we do not use the value of '%(push)' in cases where the
                // value was provided by a push refspec defined for the remote, but the local branch is not already
                // tracking some remote branch.
                string remoteRef = (match.Groups["remote_p"].Success && !string.IsNullOrEmpty(match.Groups["remote_p"].Value) && !match.Groups["gone_p"].Success)
                    ? match.Groups["remote_p"].Value
                    : match.Groups["remote_u"].Value;
                if (string.IsNullOrEmpty(branch) || string.IsNullOrEmpty(remoteRef))
                {
                    continue;
                }

#pragma warning disable SA1515 // Single-line comment should be preceded by blank line
                aheadBehindForBranchesData.Add(match.Groups["branch"].Value,
                    new AheadBehindData
                    {
                        // The information is displayed in the push button, so the push info is preferred (may differ from upstream)
                        Branch = branch,
                        RemoteRef = remoteRef,
                        AheadCount =
                            // Prefer push to upstream for the count
                            match.Groups["ahead_p"].Success
                            // Single-line comment should be preceded by blank line
                            ? match.Groups["ahead_p"].Value
                            // If behind is set for push, ahead is null
                            : match.Groups["behind_p"].Success
                            ? string.Empty
                            : match.Groups["ahead_u"].Success
                            ? match.Groups["ahead_u"].Value
                            // No information about the remote branch, it is gone
                            : match.Groups["gone_p"].Success || match.Groups["gone_u"].Success
                            ? AheadBehindData.Gone
                            // If the printout is unknown (translated?), do not assume that there are "0" changes
                            : (match.Groups["unk_p"].Success && !string.IsNullOrWhiteSpace(match.Groups["unk_p"].Value))
                                || (match.Groups["unk_u"].Success && !string.IsNullOrWhiteSpace(match.Groups["unk_u"].Value))
                            ? string.Empty
                            // A remote exists, but "track" does not display the count if ahead/behind match
                            : "0",

                        // Behind do not track '0' or 'gone', only in Ahead
                        BehindCount = match.Groups["behind_p"].Success
                            ? match.Groups["behind_p"].Value
                            : !match.Groups["ahead_p"].Success
                            ? match.Groups["behind_u"].Value
                            : string.Empty
                    });
#pragma warning restore SA1515
            }

            return aheadBehindForBranchesData;
        }

        private IExecutable GetGitExecutable()
        {
            IExecutable executable = _getGitExecutable();

            if (executable is null)
            {
                throw new ArgumentException($"Require a valid instance of {nameof(IExecutable)}");
            }

            return executable;
        }

        internal TestAccessor GetTestAccessor()
            => new(this);

        internal readonly struct TestAccessor
        {
            private readonly AheadBehindDataProvider _provider;

            public TestAccessor(AheadBehindDataProvider provider)
            {
                _provider = provider;
            }

            public IDictionary<string, AheadBehindData>? GetData(Encoding encoding, string branchName) => _provider.GetData(encoding, branchName);
        }
    }
}
