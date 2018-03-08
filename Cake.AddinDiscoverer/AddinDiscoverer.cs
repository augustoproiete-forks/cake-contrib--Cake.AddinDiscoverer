﻿using AngleSharp;
using net.r_eg.MvsSln;
using Newtonsoft.Json;
using Octokit;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using ShellProgressBar;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using YamlDotNet.RepresentationModel;

namespace Cake.AddinDiscoverer
{
	public class AddinDiscoverer
	{
		private const int NUMBER_OF_STEPS = 15;
		private const string PRODUCT_NAME = "Cake.AddinDiscoverer";
		private const string ISSUE_TITLE = "Recommended changes resulting from automated audit";
		private const string CAKECONTRIB_ICON_URL = "https://cdn.rawgit.com/cake-contrib/graphics/a5cf0f881c390650144b2243ae551d5b9f836196/png/cake-contrib-medium.png";

		private readonly Options _options;
		private readonly string _tempFolder;
		private readonly IGitHubClient _githubClient;

		public AddinDiscoverer(Options options)
		{
			_options = options;
			_tempFolder = Path.Combine(_options.TemporaryFolder, PRODUCT_NAME);

			var credentials = new Credentials(_options.GithubUsername, _options.GithuPassword);
			var connection = new Connection(new ProductHeaderValue(PRODUCT_NAME))
			{
				Credentials = credentials
			};
			_githubClient = new GitHubClient(connection);
		}

		public async Task LaunchDiscoveryAsync()
		{
			try
			{
				if (_options.ClearCache && Directory.Exists(_tempFolder))
				{
					Directory.Delete(_tempFolder, true);
					await Task.Delay(1000).ConfigureAwait(false);
				}

				if (!Directory.Exists(_tempFolder))
				{
					Directory.CreateDirectory(_tempFolder);
					await Task.Delay(1000).ConfigureAwait(false);
				}

				var progressBarOptions = new ProgressBarOptions
				{
					ForegroundColor = ConsoleColor.Yellow,
					BackgroundColor = ConsoleColor.DarkYellow,
					ProgressCharacter = '─',
					ProgressBarOnBottom = true
				};
				using (var progressBar = new ProgressBar(NUMBER_OF_STEPS, "Discovering the Cake Addins", progressBarOptions))
				{
					var jsonSaveLocation = Path.Combine(_tempFolder, "CakeAddins.json");

					var normalizedAddins = File.Exists(jsonSaveLocation) ?
						JsonConvert.DeserializeObject<AddinMetadata[]>(File.ReadAllText(jsonSaveLocation)) :
						Enumerable.Empty<AddinMetadata>();

					if (normalizedAddins.Any())
					{
						progressBar.Tick();
						progressBar.Tick();
					}
					else
					{
						// Step 1 - Discover Cake Addins by going through the '.yml' files in https://github.com/cake-build/website/tree/develop/addins
						var addinsDiscoveredByYaml = await DiscoverCakeAddinsByYmlAsync(progressBar).ConfigureAwait(false);
						progressBar.Tick();

						// Step 2 - Discover Cake addins by looking at the "Recipe", "Modules" and "Addins" section in 'https://raw.githubusercontent.com/cake-contrib/Home/master/Status.md'
						var addinsDiscoveredByGep13List = await DiscoverCakeAddinsByWebsiteList(progressBar).ConfigureAwait(false);
						progressBar.Tick();

						// Combine all the discovered addins
						var allDiscoveredAddins = addinsDiscoveredByYaml
							.Union(addinsDiscoveredByGep13List)
							.ToArray();

						// Remove duplicates
						var gitHubAddins = allDiscoveredAddins
							.GroupBy(p => p.Name.ToLower())
							.Select(grp => grp.FirstOrDefault(p => p.IsValid()))
							.Where(p => p != null)
							.ToArray();

						var nugetAddins = allDiscoveredAddins
							.Where(p => p.RepositoryUrl != null)
							.GroupBy(p => p.Name.ToLower())
							.Where(grp => !grp.Any(p => p.IsValid()))
							.Select(grp => grp.First())
							.ToArray();

						normalizedAddins = gitHubAddins
							.Union(nugetAddins)
							.ToArray();
					}

					// Step 3 - reset the summary
					normalizedAddins = ResetSummaryAsync(normalizedAddins, progressBar);
					File.WriteAllText(jsonSaveLocation, JsonConvert.SerializeObject(normalizedAddins));
					progressBar.Tick();

					// Step 4 - normalize the addin URL so it points to the github repo (instead of nuget)
					normalizedAddins = await NormalizeAddinUrlAsync(normalizedAddins, progressBar).ConfigureAwait(false);
					File.WriteAllText(jsonSaveLocation, JsonConvert.SerializeObject(normalizedAddins));
					progressBar.Tick();

					// Step 5 - get the path to the .sln file in the github repo
					// Please note: we use the first solution file if there is more than one
					normalizedAddins = await FindSolutionPathAsync(normalizedAddins, progressBar).ConfigureAwait(false);
					File.WriteAllText(jsonSaveLocation, JsonConvert.SerializeObject(normalizedAddins));
					progressBar.Tick();

					// Step 6 - get the path to the .csproj file(s)
					normalizedAddins = await FindProjectPathAsync(normalizedAddins, progressBar).ConfigureAwait(false);
					File.WriteAllText(jsonSaveLocation, JsonConvert.SerializeObject(normalizedAddins));
					progressBar.Tick();

					// Step 7 - download a copy of the csproj file(s) which simplyfies parsing this file in subsequent steps
					await DownloadProjectFilesAsync(normalizedAddins, progressBar).ConfigureAwait(false);
					progressBar.Tick();

					// Step 8 - parse the csproj and find all references
					normalizedAddins = await FindReferencesAsync(normalizedAddins, progressBar).ConfigureAwait(false);
					File.WriteAllText(jsonSaveLocation, JsonConvert.SerializeObject(normalizedAddins));
					progressBar.Tick();

					// Step 9 - parse the csproj and find targeted framework(s)
					normalizedAddins = await FindFrameworksAsync(normalizedAddins, progressBar).ConfigureAwait(false);
					File.WriteAllText(jsonSaveLocation, JsonConvert.SerializeObject(normalizedAddins));
					progressBar.Tick();

					// Step 10 - determine if an issue already exists in the Github repo
					normalizedAddins = await FindGithubIssueAsync(normalizedAddins, progressBar).ConfigureAwait(false);
					File.WriteAllText(jsonSaveLocation, JsonConvert.SerializeObject(normalizedAddins));
					progressBar.Tick();

					// Step 11 - find the addin icon
					normalizedAddins = await FindIconAsync(normalizedAddins, progressBar).ConfigureAwait(false);
					File.WriteAllText(jsonSaveLocation, JsonConvert.SerializeObject(normalizedAddins));
					progressBar.Tick();

					// Step 12 - analyze
					normalizedAddins = AnalyzeAddinAsync(normalizedAddins, progressBar);
					File.WriteAllText(jsonSaveLocation, JsonConvert.SerializeObject(normalizedAddins));
					progressBar.Tick();

					// Step 13 - create an issue in the Github repo
					if (_options.CreateGithubIssue)
					{
						normalizedAddins = await CreateGithubIssueAsync(normalizedAddins, progressBar).ConfigureAwait(false);
						File.WriteAllText(jsonSaveLocation, JsonConvert.SerializeObject(normalizedAddins));
					}
					progressBar.Tick();

					// Step 14 - generate the excel report
					if (_options.GenerateExcelReport) GenerateExcelReport(normalizedAddins, progressBar);
					progressBar.Tick();

					// Step 15 - generate the markdown report
					if (_options.GenerateMarkdownReport) GenerateMarkdownReport(normalizedAddins, progressBar);
					progressBar.Tick();
				}
			}
			catch (Exception e)
			{
				Console.WriteLine(e.GetBaseException().Message);
			}
		}

		private async Task<AddinMetadata[]> DiscoverCakeAddinsByYmlAsync(IProgressBar parentProgressBar)
		{
			// Get the list of yaml files in the 'addins' folder
			var directoryContent = await _githubClient.Repository.Content.GetAllContents("cake-build", "website", "addins").ConfigureAwait(false);
			var yamlFiles = directoryContent
				.Where(c => c.Name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
				.ToArray();

			// Spawn a progressbar to display progress
			var childOptions = new ProgressBarOptions
			{
				CollapseWhenFinished = false,
				ForegroundColor = ConsoleColor.Green,
				BackgroundColor = ConsoleColor.DarkGreen,
				ProgressCharacter = '─',
				ProgressBarOnBottom = true
			};
			using (var progressBar = parentProgressBar.Spawn(yamlFiles.Length, "Discovering Cake addins by yml", childOptions))
			{
				// Get the content of the yaml files
				var tasks = yamlFiles
					.Select(async file =>
					{
						// Get the content
						var fileWithContent = await _githubClient.Repository.Content.GetAllContents("cake-build", "website", file.Path).ConfigureAwait(false);

						// Parse the content
						var yaml = new YamlStream();
						yaml.Load(new StringReader(fileWithContent[0].Content));

						// Extract Author, Description, Name and repository URL
						var yamlRootNode = yaml.Documents[0].RootNode;
						var metadata = new AddinMetadata()
						{
							Source = AddinMetadataSource.Yaml,
							Name = yamlRootNode["Name"].ToString(),
							RepositoryUrl = new Uri(yamlRootNode["Repository"].ToString())
						};

						progressBar.Tick();

						return metadata;
					});

				var filesWithContent = await Task.WhenAll(tasks).ConfigureAwait(false);
				return filesWithContent.ToArray();
			}
		}

		private async Task<AddinMetadata[]> DiscoverCakeAddinsByWebsiteList(IProgressBar parentProgressBar)
		{
			// Get the content of the 'Status.md' file
			var statusFile = await _githubClient.Repository.Content.GetAllContents("cake-contrib", "home", "Status.md").ConfigureAwait(false);
			var statusFileContent = statusFile[0].Content;

			// Get the "recipes", "modules" and "Addins"
			var childOptions = new ProgressBarOptions
			{
				CollapseWhenFinished = false,
				ForegroundColor = ConsoleColor.Green,
				BackgroundColor = ConsoleColor.DarkGreen,
				ProgressCharacter = '─',
				ProgressBarOnBottom = true
			};
			using (var progressBar = parentProgressBar.Spawn(3, "Discovering Cake addins by parsing the Gep13 list", childOptions))
			{
				// The status.md file contains several sections such as "Recipes", "Modules", "Websites", "Addins", 
				// "Work In Progress", "Needs Investigation" and "Deprecated". I am making the assumption that we
				// only care about 3 of those sections: "Recipes", "Modules" and "Addins".

				var recipes = GetAddins("Recipes", statusFileContent, progressBar).ToArray();
				progressBar.Tick();

				var modules = GetAddins("Modules", statusFileContent, progressBar).ToArray();
				progressBar.Tick();

				var addins = GetAddins("Addins", statusFileContent, progressBar).ToArray();
				progressBar.Tick();

				// Combine the three lists
				return recipes
					.Union(modules)
					.Union(addins)
					.ToArray();
			}
		}

		private AddinMetadata[] ResetSummaryAsync(IEnumerable<AddinMetadata> addins, IProgressBar parentProgressBar)
		{
			// Spawn a progressbar to display progress
			var childOptions = new ProgressBarOptions
			{
				CollapseWhenFinished = false,
				ForegroundColor = ConsoleColor.Green,
				BackgroundColor = ConsoleColor.DarkGreen,
				ProgressCharacter = '─',
				ProgressBarOnBottom = true
			};
			using (var progressBar = parentProgressBar.Spawn(addins.Count(), "Clear previous summary", childOptions))
			{
				var results = addins
					.Select(addin =>
					{
						addin.AnalysisResult = new AddinAnalysisResult();
						progressBar.Tick();
						return addin;
					});

				return results.ToArray();
			}
		}

		private async Task<AddinMetadata[]> NormalizeAddinUrlAsync(IEnumerable<AddinMetadata> addins, IProgressBar parentProgressBar)
		{
			// Spawn a progressbar to display progress
			var childOptions = new ProgressBarOptions
			{
				CollapseWhenFinished = false,
				ForegroundColor = ConsoleColor.Green,
				BackgroundColor = ConsoleColor.DarkGreen,
				ProgressCharacter = '─',
				ProgressBarOnBottom = true
			};
			using (var progressBar = parentProgressBar.Spawn(addins.Count(), "Normalize addin URL", childOptions))
			{
				var tasks = addins
					.Select(async addin =>
					{
						if (!addin.IsValid())
						{
							try
							{
								addin.RepositoryUrl = await GetNormalizedProjectUrl(addin.RepositoryUrl).ConfigureAwait(false);
							}
							catch (Exception e)
							{
								addin.AnalysisResult.Notes += $"NormalizeAddinUrlAsync: {e.GetBaseException().Message}\r\n";
							}
						}
						progressBar.Tick();
						return addin;
					});

				var results = await Task.WhenAll(tasks).ConfigureAwait(false);
				return results.ToArray();
			}
		}

		private async Task<AddinMetadata[]> FindSolutionPathAsync(IEnumerable<AddinMetadata> addins, IProgressBar parentProgressBar)
		{
			// Spawn a progressbar to display progress
			var childOptions = new ProgressBarOptions
			{
				CollapseWhenFinished = false,
				ForegroundColor = ConsoleColor.Green,
				BackgroundColor = ConsoleColor.DarkGreen,
				ProgressCharacter = '─',
				ProgressBarOnBottom = true
			};
			using (var progressBar = parentProgressBar.Spawn(addins.Count(), "Find the .SLN", childOptions))
			{
				var tasks = addins
					.Select(async addin =>
					{
						try
						{
							if (addin.IsValid() && string.IsNullOrEmpty(addin.SolutionPath))
							{
								var solutionFile = await GetSolutionFileAsync(addin).ConfigureAwait(false);
								addin.SolutionPath = solutionFile.Path;
							}
						}
						catch (NotFoundException)
						{
							addin.AnalysisResult.Notes += $"The project does not exist: {addin.RepositoryUrl}\r\n";
						}
						catch (Exception e)
						{
							addin.AnalysisResult.Notes += $"FindSolutionPathAsync: {e.GetBaseException().Message}\r\n";
						}
						progressBar.Tick();
						return addin;
					});

				var results = await Task.WhenAll(tasks).ConfigureAwait(false);
				return results.ToArray();
			}
		}

		private async Task<AddinMetadata[]> FindProjectPathAsync(IEnumerable<AddinMetadata> addins, IProgressBar parentProgressBar)
		{
			// Spawn a progressbar to display progress
			var childOptions = new ProgressBarOptions
			{
				CollapseWhenFinished = false,
				ForegroundColor = ConsoleColor.Green,
				BackgroundColor = ConsoleColor.DarkGreen,
				ProgressCharacter = '─',
				ProgressBarOnBottom = true
			};
			using (var progressBar = parentProgressBar.Spawn(addins.Count(), "Find the .csproj path", childOptions))
			{
				var tasks = addins
					.Select(async addin =>
					{
						if (!string.IsNullOrEmpty(addin.SolutionPath) && addin.ProjectPaths == null)
						{
							try
							{
								var solutionFile = await _githubClient.Repository.Content.GetAllContents(addin.GithubRepoOwner, addin.GithubRepoName, addin.SolutionPath).ConfigureAwait(false);

								using (var sln = new Sln(SlnItems.Projects, solutionFile[0].Content))
								{
									if (sln.Result.ProjectItems != null)
									{
										var solutionParts = addin.SolutionPath.Split('/');

										addin.ProjectPaths = sln.Result.ProjectItems
											.Select(p => string.Join('/', solutionParts.Take(solutionParts.Length - 1).Union(p.path.Split('\\'))))
											.Where(p => !p.EndsWith(".Tests.csproj"))
											.ToArray();
									}
									else
									{
										addin.AnalysisResult.Notes += $"The solution file does not reference any project: {solutionFile[0].HtmlUrl}\r\n";
									}
								}
							}
							catch (Exception e)
							{
								addin.AnalysisResult.Notes += $"FindProjectPathAsync: {e.GetBaseException().Message}\r\n";
							}
						}

						progressBar.Tick();
						return addin;
					});

				var results = await Task.WhenAll(tasks).ConfigureAwait(false);
				return results.ToArray();
			}
		}

		private async Task DownloadProjectFilesAsync(IEnumerable<AddinMetadata> addins, IProgressBar parentProgressBar)
		{
			// Spawn a progressbar to display progress
			var childOptions = new ProgressBarOptions
			{
				CollapseWhenFinished = false,
				ForegroundColor = ConsoleColor.Green,
				BackgroundColor = ConsoleColor.DarkGreen,
				ProgressCharacter = '─',
				ProgressBarOnBottom = true
			};
			using (var progressBar = parentProgressBar.Spawn(addins.Count(), "Download project files", childOptions))
			{
				var tasks = addins
					.Select(async addin =>
					{
						if (addin.ProjectPaths != null)
						{
							var folderLocation = Path.Combine(_tempFolder, addin.Name);
							Directory.CreateDirectory(folderLocation);

							foreach (var projectPath in addin.ProjectPaths)
							{
								try
								{
									var fileName = Path.Combine(folderLocation, Path.GetFileName(projectPath));
									if (!File.Exists(fileName))
									{
										var content = await _githubClient.Repository.Content.GetAllContents(addin.GithubRepoOwner, addin.GithubRepoName, projectPath).ConfigureAwait(false);
										File.WriteAllText(fileName, content[0].Content);
									}
								}
								catch (Exception e)
								{
									addin.AnalysisResult.Notes += $"DownloadProjectFilesAsync: {e.GetBaseException().Message}\r\n";
								}
							}
						}
						progressBar.Tick();
					});

				await Task.WhenAll(tasks).ConfigureAwait(false);
			}
		}

		private async Task<AddinMetadata[]> FindReferencesAsync(IEnumerable<AddinMetadata> addins, IProgressBar parentProgressBar)
		{
			// Spawn a progressbar to display progress
			var childOptions = new ProgressBarOptions
			{
				CollapseWhenFinished = false,
				ForegroundColor = ConsoleColor.Green,
				BackgroundColor = ConsoleColor.DarkGreen,
				ProgressCharacter = '─',
				ProgressBarOnBottom = true
			};
			using (var progressBar = parentProgressBar.Spawn(addins.Count(), "Finding references", childOptions))
			{
				var tasks = addins
					.Select(async addin =>
					{
						var references = new List<(string Id, string Version, bool IsPrivate)>();
						var folderName = Path.Combine(_tempFolder, addin.Name);

						if (Directory.Exists(folderName))
						{
							foreach (var projectPath in Directory.EnumerateFiles(folderName))
							{
								try
								{
									references.AddRange(await GetProjectReferencesAsync(addin, projectPath).ConfigureAwait(false));
								}
								catch (Exception e)
								{
									addin.AnalysisResult.Notes += $"FindReferencesAsync: {e.GetBaseException().Message}\r\n";
								}
							}
						}

						addin.References = references
							.GroupBy(r => r.Id)
							.Select(grp => (grp.Key, grp.Min(r => r.Version), grp.All(r => r.IsPrivate)))
							.ToArray();

						progressBar.Tick();
						return addin;
					});

				var results = await Task.WhenAll(tasks).ConfigureAwait(false);
				return results.ToArray();
			}
		}

		private async Task<AddinMetadata[]> FindFrameworksAsync(IEnumerable<AddinMetadata> addins, IProgressBar parentProgressBar)
		{
			// Spawn a progressbar to display progress
			var childOptions = new ProgressBarOptions
			{
				CollapseWhenFinished = false,
				ForegroundColor = ConsoleColor.Green,
				BackgroundColor = ConsoleColor.DarkGreen,
				ProgressCharacter = '─',
				ProgressBarOnBottom = true
			};
			using (var progressBar = parentProgressBar.Spawn(addins.Count(), "Find Frameworks", childOptions))
			{
				var tasks = addins
					.Select(async addin =>
					{
						var frameworks = new List<string>();
						var folderName = Path.Combine(_tempFolder, addin.Name);

						if (Directory.Exists(folderName))
						{
							foreach (var projectPath in Directory.EnumerateFiles(Path.Combine(_tempFolder, addin.Name)))
							{
								try
								{
									frameworks.AddRange(await GetProjectFrameworksAsync(addin, projectPath).ConfigureAwait(false));
								}
								catch (Exception e)
								{
									addin.AnalysisResult.Notes += $"FindFrameworksAsync: {e.GetBaseException().Message}\r\n";
								}
							}
						}

						addin.Frameworks = frameworks
							.GroupBy(f => f)
							.Select(grp => grp.First())
							.ToArray();

						progressBar.Tick();
						return addin;
					});

				var results = await Task.WhenAll(tasks).ConfigureAwait(false);
				return results.ToArray();
			}
		}

		private async Task<AddinMetadata[]> FindGithubIssueAsync(IEnumerable<AddinMetadata> addins, IProgressBar parentProgressBar)
		{
			// Spawn a progressbar to display progress
			var childOptions = new ProgressBarOptions
			{
				CollapseWhenFinished = false,
				ForegroundColor = ConsoleColor.Green,
				BackgroundColor = ConsoleColor.DarkGreen,
				ProgressCharacter = '─',
				ProgressBarOnBottom = true
			};
			using (var progressBar = parentProgressBar.Spawn(addins.Count(), "Find Github issue", childOptions))
			{
				var tasks = addins
					.Select(async addin =>
					{
						if (addin.GithubIssueUrl == null && addin.IsValid())
						{
							var request = new RepositoryIssueRequest()
							{
								Creator = _options.GithubUsername,
								State = ItemStateFilter.Open,
								SortProperty = IssueSort.Created,
								SortDirection = SortDirection.Descending
							};

							try
							{
								var issues = await _githubClient.Issue.GetAllForRepository(addin.GithubRepoOwner, addin.GithubRepoName, request).ConfigureAwait(false);
								var issue = issues.FirstOrDefault(i => i.Title == ISSUE_TITLE);

								if (issue != null)
								{
									addin.GithubIssueUrl = new Uri(issue.Url);
									addin.GithubIssueId = issue.Number;
								}
							}
							catch (Exception e)
							{
								addin.AnalysisResult.Notes += $"FindGithubIssueAsync: {e.GetBaseException().Message}\r\n";
							}
						}

						progressBar.Tick();
						return addin;
					});

				var results = await Task.WhenAll(tasks).ConfigureAwait(false);
				return results.ToArray();
			}
		}

		private async Task<AddinMetadata[]> FindIconAsync(IEnumerable<AddinMetadata> addins, IProgressBar parentProgressBar)
		{
			// Spawn a progressbar to display progress
			var childOptions = new ProgressBarOptions
			{
				CollapseWhenFinished = false,
				ForegroundColor = ConsoleColor.Green,
				BackgroundColor = ConsoleColor.DarkGreen,
				ProgressCharacter = '─',
				ProgressBarOnBottom = true
			};
			using (var progressBar = parentProgressBar.Spawn(addins.Count(), "Finding icon", childOptions))
			{
				var tasks = addins
					.Select(async addin =>
					{
						var folderName = Path.Combine(_tempFolder, addin.Name);

						if (Directory.Exists(folderName))
						{
							foreach (var projectPath in Directory.EnumerateFiles(folderName))
							{
								try
								{
									using (var stream = File.OpenText(projectPath))
									{
										var document = await XDocument.LoadAsync(stream, LoadOptions.None, CancellationToken.None).ConfigureAwait(false);
										var icon = document.Descendants("PackageIconUrl").FirstOrDefault();
										if (icon != null && !string.IsNullOrEmpty(icon.Value)) addin.IconUrl = new Uri(icon.Value);
									}
								}
								catch (Exception e)
								{
									addin.AnalysisResult.Notes += $"FindIconAsync: {e.GetBaseException().Message}\r\n";
								}
							}
						}

						progressBar.Tick();
						return addin;
					});

				var results = await Task.WhenAll(tasks).ConfigureAwait(false);
				return results.ToArray();
			}
		}

		private AddinMetadata[] AnalyzeAddinAsync(IEnumerable<AddinMetadata> addins, IProgressBar parentProgressBar)
		{
			// Spawn a progressbar to display progress
			var childOptions = new ProgressBarOptions
			{
				CollapseWhenFinished = false,
				ForegroundColor = ConsoleColor.Green,
				BackgroundColor = ConsoleColor.DarkGreen,
				ProgressCharacter = '─',
				ProgressBarOnBottom = true
			};
			using (var progressBar = parentProgressBar.Spawn(addins.Count(), "Analyze addins", childOptions))
			{
				var results = addins
					.Select(addin =>
					{
						if (addin.References != null)
						{
							addin.AnalysisResult.TargetsExpectedFramework =
								(addin.Frameworks ?? Array.Empty<string>()).Length == 1 &&
								addin.Frameworks[0] == "netstandard2.0";

							var cakeCommonReference = addin.References.Where(r => r.Id == "Cake.Common");
							var cakeCommonVersion = FormatVersion(cakeCommonReference.Min(r => r.Version));
							var cakeCommonIsPrivate = cakeCommonReference.All(r => r.IsPrivate);
							addin.AnalysisResult.CakeCommonVersion = cakeCommonVersion;
							addin.AnalysisResult.CakeCommonIsPrivate = cakeCommonIsPrivate;
							addin.AnalysisResult.CakeCommonIsUpToDate = IsUpToDate(cakeCommonVersion, _options.RecommendedCakeVersion);

							var cakeCoreReference = addin.References.Where(r => r.Id == "Cake.Core");
							var cakeCoreVersion = FormatVersion(cakeCoreReference.Min(r => r.Version));
							var cakeCoreIsPrivate = cakeCoreReference.All(r => r.IsPrivate);
							addin.AnalysisResult.CakeCoreVersion = cakeCoreVersion;
							addin.AnalysisResult.CakeCoreIsPrivate = cakeCoreIsPrivate;
							addin.AnalysisResult.CakeCoreIsUpToDate = IsUpToDate(cakeCoreVersion, _options.RecommendedCakeVersion);

							addin.AnalysisResult.UsingCakeContribIcon = (addin.IconUrl != null && addin.IconUrl.AbsoluteUri == CAKECONTRIB_ICON_URL);
						}

						progressBar.Tick();
						return addin;
					});

				return results.ToArray();
			}
		}

		private async Task<AddinMetadata[]> CreateGithubIssueAsync(IEnumerable<AddinMetadata> addins, IProgressBar parentProgressBar)
		{
			// Spawn a progressbar to display progress
			var childOptions = new ProgressBarOptions
			{
				CollapseWhenFinished = false,
				ForegroundColor = ConsoleColor.Green,
				BackgroundColor = ConsoleColor.DarkGreen,
				ProgressCharacter = '─',
				ProgressBarOnBottom = true
			};
			using (var progressBar = parentProgressBar.Spawn(addins.Count(), "Analyze addins", childOptions))
			{
				var tasks = addins
					.Select(async addin =>
					{
						if (addin.GithubIssueUrl == null)
						{
							var issueBody = new StringBuilder();
							issueBody.Append("We performed an automated audit of your Cake addin and found that it does not follow all the best practices.\r\n\r\n");
							issueBody.Append("We encourage you to make the following modifications:\r\n\r\n");
							if (!string.IsNullOrEmpty(addin.AnalysisResult.CakeCoreVersion) && !addin.AnalysisResult.CakeCoreIsUpToDate)
							{
								issueBody.Append($"- [ ] You are currently referencing Cake.Core {addin.AnalysisResult.CakeCoreVersion}. Please upgrade to {_options.RecommendedCakeVersion}\r\n");
							}
							if (!string.IsNullOrEmpty(addin.AnalysisResult.CakeCommonVersion) && !addin.AnalysisResult.CakeCommonIsUpToDate)
							{
								issueBody.Append($"- [ ] You are currently referencing Cake.Common {addin.AnalysisResult.CakeCommonVersion}. Please upgrade to {_options.RecommendedCakeVersion}\r\n");
							}
							if (!addin.AnalysisResult.CakeCoreIsPrivate) issueBody.Append("- [ ] The Cake.Core reference should be private\r\n");
							if (!addin.AnalysisResult.CakeCommonIsPrivate) issueBody.Append("- [ ] The Cake.Common reference should be private\r\n");
							if (!addin.AnalysisResult.TargetsExpectedFramework) issueBody.Append("- [ ] Your addin should target netstandard2.0\r\n(Please note: there is no need to multi-target. As of Cake 0.26.0, netstandard2.0 is sufficient)\r\n");

							var newIssue = new NewIssue(ISSUE_TITLE)
							{
								Body = issueBody.ToString()
							};
							var issue = await _githubClient.Issue.Create(addin.GithubRepoOwner, addin.GithubRepoName, newIssue).ConfigureAwait(false);
							addin.GithubIssueUrl = new Uri(issue.Url);
							addin.GithubIssueId = issue.Number;
						}

						progressBar.Tick();
						return addin;
					});

				var results = await Task.WhenAll(tasks).ConfigureAwait(false);
				return results.ToArray();
			}
		}

		private void GenerateExcelReport(IEnumerable<AddinMetadata> addins, IProgressBar parentProgressBar)
		{
			// Spawn a progressbar to display progress
			var childOptions = new ProgressBarOptions
			{
				CollapseWhenFinished = false,
				ForegroundColor = ConsoleColor.Green,
				BackgroundColor = ConsoleColor.DarkGreen,
				ProgressCharacter = '─',
				ProgressBarOnBottom = true
			};
			using (var progressBar = parentProgressBar.Spawn(addins.Count(), "Generating Excel report", childOptions))
			{
				var reportSaveLocation = Path.Combine(_tempFolder, "AddinDiscoveryReport.xlsx");

				FileInfo file = new FileInfo(reportSaveLocation);

				if (file.Exists)
				{
					file.Delete();
					file = new FileInfo(reportSaveLocation);
				}

				using (var package = new ExcelPackage(file))
				{
					var namedStyle = package.Workbook.Styles.CreateNamedStyle("HyperLink");
					namedStyle.Style.Font.UnderLine = true;
					namedStyle.Style.Font.Color.SetColor(Color.Blue);

					var worksheet = package.Workbook.Worksheets.Add("Addins");

					worksheet.Cells[1, 2].Value = "Cake Core";
					worksheet.Cells[1, 2, 1, 3].Merge = true;

					worksheet.Cells[1, 4].Value = "Cake Common";
					worksheet.Cells[1, 4, 1, 5].Merge = true;

					worksheet.Cells[2, 1].Value = "Addin";
					worksheet.Cells[2, 2].Value = "Version";
					worksheet.Cells[2, 3].Value = "IsPrivate";
					worksheet.Cells[2, 4].Value = "Version";
					worksheet.Cells[2, 5].Value = "IsPrivate";
					worksheet.Cells[2, 6].Value = "Framework";
					worksheet.Cells[2, 7].Value = "Icon";
					worksheet.Cells[2, 8].Value = "Notes";

					var row = 2;
					foreach (var addin in addins.OrderBy(p => p.Name))
					{
						row++;
						worksheet.Cells[row, 1].Value = addin.Name;
						worksheet.Cells[row, 1].Hyperlink = addin.RepositoryUrl;
						worksheet.Cells[row, 1].StyleName = "HyperLink";

						if (!string.IsNullOrEmpty(addin.AnalysisResult.CakeCoreVersion))
						{
							DisplayValueWithExpectation(worksheet.Cells[row, 2], FormatVersion(addin.AnalysisResult.CakeCoreVersion), addin.AnalysisResult.CakeCoreIsUpToDate);
							DisplayValueWithExpectation(worksheet.Cells[row, 3], addin.AnalysisResult.CakeCoreIsPrivate);
						}

						if (!string.IsNullOrEmpty(addin.AnalysisResult.CakeCommonVersion))
						{
							DisplayValueWithExpectation(worksheet.Cells[row, 4], FormatVersion(addin.AnalysisResult.CakeCommonVersion), addin.AnalysisResult.CakeCommonIsUpToDate);
							DisplayValueWithExpectation(worksheet.Cells[row, 5], addin.AnalysisResult.CakeCommonIsPrivate);
						}

						if ((addin.Frameworks?.Length ?? 0) != 0)
						{
							DisplayValueWithExpectation(worksheet.Cells[row, 6], string.Join(", ", addin.Frameworks), addin.AnalysisResult.TargetsExpectedFramework);
						}

						DisplayValueWithExpectation(worksheet.Cells[row, 7], addin.AnalysisResult.UsingCakeContridIcon);

						worksheet.Cells[row, 8].Value = addin.AnalysisResult.Notes;

						progressBar.Tick();
					}

					// Freeze the top two rows and setup auto-filter
					worksheet.View.FreezePanes(3, 1);
					worksheet.Cells[2, 1, 2, 8].AutoFilter = true;

					// Format the worksheet
					worksheet.Row(1).Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
					worksheet.Row(2).Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
					worksheet.Column(2).Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
					worksheet.Column(3).Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
					worksheet.Column(4).Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
					worksheet.Column(5).Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
					worksheet.Column(6).Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
					worksheet.Column(7).Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;

					// Resize columns
					worksheet.Cells[2, 1, row, 8].AutoFitColumns();

					// Make columns a little bit wider to account for the filter "drop-down arrow" button
					worksheet.Column(1).Width = worksheet.Column(1).Width + 1;
					worksheet.Column(2).Width = worksheet.Column(2).Width + 2.14;
					worksheet.Column(3).Width = worksheet.Column(3).Width + 2.14;
					worksheet.Column(4).Width = worksheet.Column(4).Width + 2.14;
					worksheet.Column(5).Width = worksheet.Column(5).Width + 2.14;
					worksheet.Column(6).Width = worksheet.Column(6).Width + 1;
					worksheet.Column(7).Width = worksheet.Column(7).Width + 2.14;

					// Save the Excel file
					package.Save();
				}
			}
		}

		private void GenerateMarkdownReport(IEnumerable<AddinMetadata> addins, IProgressBar parentProgressBar)
		{
			// Spawn a progressbar to display progress
			var childOptions = new ProgressBarOptions
			{
				CollapseWhenFinished = false,
				ForegroundColor = ConsoleColor.Green,
				BackgroundColor = ConsoleColor.DarkGreen,
				ProgressCharacter = '─',
				ProgressBarOnBottom = true
			};
			using (var progressBar = parentProgressBar.Spawn(addins.Count(), "Generating markdown", childOptions))
			{
				var markdown = new StringBuilder();
				var columns = new(string Header, int Width, string Align)[]
				{
					("Addin", addins.Max(a => a.Name.Length + a.RepositoryUrl.AbsoluteUri.Length + 4), "left"),
					("Cake Core Version", addins.Max(a => a.AnalysisResult.CakeCoreVersion.Length), "Center"),
					("Cake Core IsPrivate", addins.Max(a => a.AnalysisResult.CakeCoreIsPrivate.ToString().Length), "Center"),
					("Cake Common Version", addins.Max(a => a.AnalysisResult.CakeCommonVersion.Length), "Center"),
					("Cake Common IsPrivate", addins.Max(a => a.AnalysisResult.CakeCommonIsPrivate.ToString().Length), "Center"),
					("Framework", addins.Max(a => string.Join(", ", a.Frameworks).Length), "Center"),
					("Icon", addins.Max(a => a.AnalysisResult.UsingCakeContridIcon.ToString().Length), "Center"),
				};

				for (int i = 0; i < columns.Length; i++)
				{
					// Ensure columns are wide enough to display the header
					columns[i].Width = Math.Max(columns[i].Header.Length, columns[i].Width);

					// Account for the column seperator
					columns[i].Width++;
				}

				foreach (var column in columns)
				{
					markdown.Append(WithRightPadding($"|{column.Header}", column.Width));
				}
				markdown.AppendLine("|");

				foreach (var column in columns)
				{
					var width = column.Width - 1;                    // Minus one for the column seperator
					if (column.Align == "Right") width = width - 1;  // Minus one for the ":" character
					if (column.Align == "Center") width = width - 2; // Minus two for the two ":" characters

					markdown.Append("|");
					if (column.Align == "Center") markdown.Append(":");
					markdown.Append(new string('-', width));
					if (column.Align == "Right" || column.Align == "Center") markdown.Append(":");
				}
				markdown.AppendLine("|");

				foreach (var addin in addins.OrderBy(p => p.Name))
				{
					markdown.Append(WithRightPadding($"|[{addin.Name}]({addin.RepositoryUrl.AbsoluteUri})", columns[0].Width));
					markdown.Append(WithRightPadding($"|{addin.AnalysisResult.CakeCoreVersion}", columns[1].Width));
					if (string.IsNullOrEmpty(addin.AnalysisResult.CakeCoreVersion))
					{
						markdown.Append(WithRightPadding("|", columns[2].Width));
					}
					else
					{
						markdown.Append(WithRightPadding($"|{addin.AnalysisResult.CakeCoreIsPrivate.ToString().ToLower()}", columns[2].Width));
					}
					markdown.Append(WithRightPadding($"|{addin.AnalysisResult.CakeCommonVersion}", columns[3].Width));
					if (string.IsNullOrEmpty(addin.AnalysisResult.CakeCommonVersion))
					{
						markdown.Append(WithRightPadding("|", columns[4].Width));
					}
					else
					{
						markdown.Append(WithRightPadding($"|{addin.AnalysisResult.CakeCommonIsPrivate.ToString().ToLower()}", columns[4].Width));
					}
					markdown.Append(WithRightPadding($"|{string.Join(", ", addin.Frameworks)}", columns[5].Width));
					markdown.Append(WithRightPadding($"|{addin.AnalysisResult.UsingCakeContridIcon.ToString().ToLower()}", columns[6].Width));
					markdown.AppendLine("|");

					progressBar.Tick();
				}

				// Save the markdown file
				var reportSaveLocation = Path.Combine(_tempFolder, "AddinDiscoveryReport.md");
				var file = new StreamWriter(reportSaveLocation);
				file.WriteLine(markdown.ToString());
			}
		}

		private static void DisplayValueWithExpectation(ExcelRange cell, string value, bool meetsExpectation)
		{
			cell.Value = value;
			cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
			cell.Style.Fill.BackgroundColor.SetColor(meetsExpectation ? Color.LightGreen : Color.OrangeRed);
		}

		private static void DisplayValueWithExpectation(ExcelRange cell, bool value)
		{
			DisplayValueWithExpectation(cell, value.ToString().ToLower(), value);
		}

		private async Task<RepositoryContent> GetSolutionFileAsync(AddinMetadata addin, string folderName = null)
		{
			var directoryContent = string.IsNullOrEmpty(folderName) ?
					await _githubClient.Repository.Content.GetAllContents(addin.GithubRepoOwner, addin.GithubRepoName).ConfigureAwait(false) :
					await _githubClient.Repository.Content.GetAllContents(addin.GithubRepoOwner, addin.GithubRepoName, folderName).ConfigureAwait(false);

			var solutions = directoryContent.Where(c => c.Type == new StringEnum<ContentType>(ContentType.File) && c.Name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase));
			if (solutions.Any()) return solutions.First();

			var subFolders = directoryContent.Where(c => c.Type == new StringEnum<ContentType>(ContentType.Dir));

			var sourceSubFolders = subFolders.Where(c => c.Name.Equals("source", StringComparison.OrdinalIgnoreCase) || c.Name.Equals("src", StringComparison.OrdinalIgnoreCase));
			if (sourceSubFolders.Any())
			{
				foreach (var subFolder in sourceSubFolders)
				{
					var solutionFile = await GetSolutionFileAsync(addin, subFolder.Name).ConfigureAwait(false);
					if (solutionFile != null) return solutionFile;
				}
			}

			var allOtherSubFolders = subFolders.Except(sourceSubFolders);
			foreach (var subFolder in allOtherSubFolders)
			{
				var solutionFile = await GetSolutionFileAsync(addin, subFolder.Path).ConfigureAwait(false);
				if (solutionFile != null) return solutionFile;
			}

			return null;
		}

		/// <summary>
		/// Searches the markdown content for a table between a section title such as '# Modules' and the next section which begins with the '#' character
		/// </summary>
		/// <param name="title">The section title</param>
		/// <param name="content">The markdown content</param>
		/// <param name="parentProgressBar">The progress bar to update as we loop through the rows in the table</param>
		/// <returns></returns>
		private AddinMetadata[] GetAddins(string title, string content, IProgressBar parentProgressBar)
		{
			var sectionContent = Extract($"# {title}", "#", content);
			var lines = sectionContent.Trim('\n').Split('\n', StringSplitOptions.RemoveEmptyEntries);

			var childOptions = new ProgressBarOptions
			{
				CollapseWhenFinished = false,
				ForegroundColor = ConsoleColor.Blue,
				BackgroundColor = ConsoleColor.DarkBlue,
				ProgressCharacter = '─',
				ProgressBarOnBottom = true
			};
			using (var progressBar = parentProgressBar.Spawn(lines.Length - 2, $"Discovering {title}", childOptions))
			{
				// It's important to skip the two 'header' rows
				var results = lines
					.Skip(2)
					.Select(line =>
					{
						var cells = line.Split('|', StringSplitOptions.RemoveEmptyEntries);

						var metadata = new AddinMetadata()
						{
							Source = AddinMetadataSource.Gep13List,
							Name = Extract("[", "]", cells[0]),
							RepositoryUrl = new Uri(Extract("(", ")", cells[0]))
						};

						progressBar.Tick();

						return metadata;
					})
					.ToArray();
				return results;
			}
		}

		/// <summary>
		/// Extract a substring between two markers. For example, Extract("[", "]", "Hello [firstname]") returns "firstname".
		/// </summary>
		/// <param name="startMark">The marker</param>
		/// <param name="endMark">The marker</param>
		/// <param name="content">The content</param>
		/// <returns></returns>
		private string Extract(string startMark, string endMark, string content)
		{
			var start = content.IndexOf(startMark, StringComparison.OrdinalIgnoreCase);
			var end = content.IndexOf(endMark, start + startMark.Length);

			if (start == -1 || end == -1) return string.Empty;

			return content
				.Substring(start + startMark.Length, end - start - startMark.Length)
				.Trim();
		}

		private async Task<IEnumerable<(string Id, string Version, bool IsPrivate)>> GetProjectReferencesAsync(AddinMetadata addin, string projectPath)
		{
			var references = new List<(string Id, string Version, bool IsPrivate)>();

			using (var stream = File.OpenText(projectPath))
			{
				var document = await XDocument.LoadAsync(stream, LoadOptions.None, CancellationToken.None).ConfigureAwait(false);

				foreach (var reference in document.Descendants("PackageReference"))
				{
					var id = (string)reference.Attribute("Include");
					var version = (string)reference.Attribute("Version");
					var isPrivate = false;
					if (reference.Attribute("PrivateAssets") != null) isPrivate = (reference.Attribute("PrivateAssets").Value) == "All";
					if (reference.Element("PrivateAssets") != null) isPrivate = (reference.Element("PrivateAssets").Value) == "All";
					references.Add((id, version, isPrivate));
				}

				var xmlns = XNamespace.Get("http://schemas.microsoft.com/developer/msbuild/2003");
				foreach (var reference in document.Descendants(xmlns + "Reference"))
				{
					var isPrivate = false;
					if (reference.Element(xmlns + "Private") != null) isPrivate = ((string)reference.Element(xmlns + "Private")) == "True";

					var referenceInfo = (string)reference.Attribute("Include");
					var firstCommaPosition = referenceInfo.IndexOf(',');
					if (firstCommaPosition > 0)
					{
						var id = referenceInfo.Substring(0, firstCommaPosition);
						var version = Extract("Version=", ",", referenceInfo);
						references.Add((id, version, isPrivate));
					}
					else
					{
						references.Add((referenceInfo, "", isPrivate));
					}
				}
			}

			return references.ToArray();
		}

		private async Task<IEnumerable<string>> GetProjectFrameworksAsync(AddinMetadata addin, string projectPath)
		{
			var frameworks = new List<string>();

			using (var stream = File.OpenText(projectPath))
			{
				var document = await XDocument.LoadAsync(stream, LoadOptions.None, CancellationToken.None).ConfigureAwait(false);


				foreach (var target in document.Descendants("TargetFramework"))
				{
					frameworks.Add(target.Value);
				}

				foreach (var target in document.Descendants("TargetFrameworks"))
				{
					frameworks.AddRange(target.Value.Split(';', StringSplitOptions.RemoveEmptyEntries));
				}

				var xmlns = XNamespace.Get("http://schemas.microsoft.com/developer/msbuild/2003");
				foreach (var target in document.Descendants(xmlns + "TargetFrameworkVersion"))
				{
					frameworks.Add(target.Value);
				}

				return frameworks.ToArray();
			}
		}

		private async Task<Uri> GetNormalizedProjectUrl(Uri projectUri)
		{
			if (projectUri.Host.Contains("nuget.org"))
			{
				// Fetch the package page from nuget and look for the "Project Site" link.
				// Please note that some packages omit this information unfortunately.

				var config = Configuration.Default.WithDefaultLoader();
				var document = await BrowsingContext.New(config).OpenAsync(Url.Convert(projectUri));

				var outboundProjectUrl = document
					.QuerySelectorAll("a")
					.Where(a =>
					{
						var dataTrackAttrib = a.Attributes["data-track"];
						if (dataTrackAttrib == null) return false;
						return dataTrackAttrib.Value == "outbound-project-url";
					});
				if (!outboundProjectUrl.Any()) return projectUri;

				return new Uri(outboundProjectUrl.First().Attributes["href"].Value);
			}
			else
			{
				return projectUri;
			}
		}

		private static bool IsUpToDate(string currentVersion, string desiredVersion)
		{
			if (string.IsNullOrEmpty(currentVersion)) return true;

			var current = currentVersion.Split('.');
			var desired = desiredVersion.Split('.');

			if (int.Parse(current[0]) < int.Parse(desired[0])) return false;
			else if (int.Parse(current[1]) < int.Parse(desired[1])) return false;
			else if (int.Parse(current[2]) < int.Parse(desired[2])) return false;
			else return true;
		}

		/// <summary>
		/// Sometimes the version has 4 parts (eg: 0.26.0.0) but we only care about the first 3
		/// </summary>
		/// <param name="version"></param>
		/// <returns></returns>
		private static string FormatVersion(string version)
		{
			if (string.IsNullOrEmpty(version)) return string.Empty;
			return string.Join('.', version.Split('.').Take(3));
		}

		private static string WithRightPadding(string content, int desiredLength)
		{
			var count = Math.Max(0, desiredLength - content.Length);
			return content + new string(' ', count);
		}
	}
}
