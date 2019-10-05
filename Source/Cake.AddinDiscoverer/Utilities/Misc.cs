using Cake.AddinDiscoverer.Models;
using Cake.Incubator.StringExtensions;
using Octokit;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace Cake.AddinDiscoverer.Utilities
{
	internal static class Misc
	{
		public static bool IsFrameworkUpToDate(string[] currentFrameworks, CakeVersion desiredCakeVersion)
		{
			if (currentFrameworks == null) return false;
			else if (!currentFrameworks.Contains(desiredCakeVersion.RequiredFramework, StringComparer.InvariantCultureIgnoreCase)) return false;
			else if (currentFrameworks.Length == 1) return true;
			else if (currentFrameworks.Length == 2 && !string.IsNullOrEmpty(desiredCakeVersion.OptionalFramework) && currentFrameworks.Contains(desiredCakeVersion.OptionalFramework, StringComparer.InvariantCultureIgnoreCase)) return true;
			else return false;
		}

		public static async Task<Issue> FindGithubIssueAsync(DiscoveryContext context, string repoOwner, string repoName, string creator, string title)
		{
			// Optimization: if the creator is the current user, we can rely on the cached list of issues
			if (creator.EqualsIgnoreCase(context.GithubClient.Connection.Credentials.Login))
			{
				return context.IssuesCreatedByCurrentUser
					.Where(i =>
					{
						var (owner, name) = Misc.DeriveRepoInfo(new Uri(i.Url));
						return owner.EqualsIgnoreCase(repoOwner) && name.EqualsIgnoreCase(repoName);
					})
					.FirstOrDefault(i => i.Title.EqualsIgnoreCase(title));
			}
			else
			{
				var request = new RepositoryIssueRequest()
				{
					Creator = creator,
					State = ItemStateFilter.Open,
					SortProperty = IssueSort.Created,
					SortDirection = SortDirection.Descending
				};

				var issues = await context.GithubClient.Issue.GetAllForRepository(repoOwner, repoName, request).ConfigureAwait(false);
				var issue = issues.FirstOrDefault(i => i.Title.EqualsIgnoreCase(title));
				return issue;
			}
		}

		public static async Task<PullRequest> FindGithubPullRequestAsync(DiscoveryContext context, string repoOwner, string repoName, string creator, string title)
		{
			// Optimization: if the creator is the current user, we can rely on the cached list of pull requests
			if (creator.EqualsIgnoreCase(context.GithubClient.Connection.Credentials.Login))
			{
				return context.PullRequestsCreatedByCurrentUser
					.Where(p =>
					{
						var (owner, name) = Misc.DeriveRepoInfo(new Uri(p.Url));
						return owner.EqualsIgnoreCase(repoOwner) && name.EqualsIgnoreCase(repoName);
					})
					.FirstOrDefault(i => i.Title.EqualsIgnoreCase(title));
			}
			else
			{
				var request = new PullRequestRequest()
				{
					State = ItemStateFilter.Open,
					SortProperty = PullRequestSort.Created,
					SortDirection = SortDirection.Descending
				};

				var pullRequests = await context.GithubClient.PullRequest.GetAllForRepository(repoOwner, repoName, request).ConfigureAwait(false);
				var pullRequest = pullRequests.FirstOrDefault(pr => pr.Title.EqualsIgnoreCase(title) && pr.User.Login.EqualsIgnoreCase(creator));

				return pullRequest;
			}
		}

		public static async Task<PullRequest> CommitToNewBranchAndSubmitPullRequestAsync(DiscoveryContext context, Octokit.Repository fork, int issueNumber, string newBranchName, string pullRequestTitle, IEnumerable<(string CommitMessage, IEnumerable<string> FilesToDelete, IEnumerable<(EncodingType Encoding, string Path, string Content)> FilesToUpsert)> commits)
		{
			if (commits == null || !commits.Any()) throw new ArgumentNullException("You must provide at least one commit", nameof(commits));

			var upstream = fork.Parent;
			var defaultBranchReference = await context.GithubClient.Git.Reference.Get(context.Options.GithubUsername, fork.Name, $"heads/{fork.DefaultBranch}").ConfigureAwait(false);
			var newReference = new NewReference($"heads/{newBranchName}", defaultBranchReference.Object.Sha);
			var newBranch = await context.GithubClient.Git.Reference.Create(context.Options.GithubUsername, fork.Name, newReference).ConfigureAwait(false);

			var latestCommit = await context.GithubClient.Git.Commit.Get(context.Options.GithubUsername, fork.Name, newBranch.Object.Sha).ConfigureAwait(false);

			foreach (var (commitMessage, filesToDelete, filesToUpsert) in commits)
			{
				latestCommit = await context.GithubClient.ModifyFilesAsync(fork, latestCommit, filesToDelete, filesToUpsert, $"(GH-{issueNumber}) {commitMessage}").ConfigureAwait(false);
			}

			await context.GithubClient.Git.Reference.Update(fork.Owner.Login, fork.Name, $"heads/{newBranchName}", new ReferenceUpdate(latestCommit.Sha)).ConfigureAwait(false);

			var newPullRequest = new NewPullRequest(pullRequestTitle, $"{fork.Owner.Login}:{newBranchName}", upstream.DefaultBranch)
			{
				Body = $"This pull request was created by a tool: Cake.AddinDiscoverer version {context.Version}{Environment.NewLine}{Environment.NewLine}Resolves #{issueNumber}"
			};
			var pullRequest = await context.GithubClient.PullRequest.Create(upstream.Owner.Login, upstream.Name, newPullRequest).ConfigureAwait(false);

			return pullRequest;
		}

		public static async Task<IDictionary<string, string[]>> GetFilePathsFromRepoAsync(DiscoveryContext context, AddinMetadata addin)
		{
			var filesPathGroupedByExtension = (IDictionary<string, string[]>)null;

			var zipArchive = await context.GithubClient.Repository.Content.GetArchive(addin.RepositoryOwner, addin.RepositoryName, ArchiveFormat.Zipball).ConfigureAwait(false);
			using (var data = new MemoryStream(zipArchive))
			{
				var archive = new ZipArchive(data);
				filesPathGroupedByExtension = archive.Entries
					.Select(e => string.Join('/', e.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1)))
					.GroupBy(path => System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
					.ToDictionary(grp => grp.Key, grp => grp.ToArray());
			}

			return filesPathGroupedByExtension;
		}

		public static async Task<string> GetFileContentFromRepoAsync(DiscoveryContext context, AddinMetadata addin, string filePath)
		{
			var content = await context.GithubClient.Repository.Content.GetAllContents(addin.RepositoryOwner, addin.RepositoryName, filePath).ConfigureAwait(false);
			return content[0].Content;
		}

		public static (string Owner, string Name) DeriveRepoInfo(Uri url)
		{
			var owner = string.Empty;
			var name = string.Empty;

			if (url != null)
			{
				var parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length >= 3 && parts[0].EqualsIgnoreCase("repos"))
				{
					owner = parts[1];
					name = parts[2].TrimEnd(".git", StringComparison.OrdinalIgnoreCase);
				}
				else if (parts.Length >= 2)
				{
					owner = parts[0];
					name = parts[1].TrimEnd(".git", StringComparison.OrdinalIgnoreCase);
				}
			}

			return (owner, name);
		}

		// byte[] is implicitly convertible to ReadOnlySpan<byte>
		public static bool ByteArrayCompare(ReadOnlySpan<byte> a1, ReadOnlySpan<byte> a2)
		{
			return a1.SequenceEqual(a2);
		}
	}
}
