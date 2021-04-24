using Cake.AddinDiscoverer.Models;
using Cake.AddinDiscoverer.Utilities;
using OfficeOpenXml.Style;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Cake.AddinDiscoverer.Steps
{
	internal class GenerateMarkdownReportStep : IStep
	{
		public bool PreConditionIsMet(DiscoveryContext context) => context.Options.MarkdownReportToFile || context.Options.MarkdownReportToRepo;

		public string GetDescription(DiscoveryContext context) => "Generate the markdown report";

		public async Task ExecuteAsync(DiscoveryContext context, TextWriter log)
		{
			var categorizedAddins = context.Addins.Where(addin => addin.Type != AddinType.Unknown);
			var uncategorizedAddins = context.Addins.Where(addin => addin.Type == AddinType.Unknown);
			var deprecatedAddins = categorizedAddins.Where(addin => addin.IsDeprecated);
			var auditedAddins = categorizedAddins.Where(addin => !addin.IsDeprecated && string.IsNullOrEmpty(addin.AnalysisResult.Notes));
			var exceptionAddins = categorizedAddins.Where(addin => !addin.IsDeprecated && !string.IsNullOrEmpty(addin.AnalysisResult.Notes));

			var now = DateTime.UtcNow;
			var markdown = new StringBuilder();

			markdown.AppendLine("# Audit Report");
			markdown.AppendLine();
			markdown.AppendLine($"This report was generated by Cake.AddinDiscoverer {context.Version} on {now.ToLongDateString()} at {now.ToLongTimeString()} GMT");
			markdown.AppendLine();

			markdown.AppendLine("## Statistics");
			markdown.AppendLine();
			markdown.AppendLine($"- The analysis discovered {context.Addins.Count()} NuGet packages");
			markdown.AppendLine($"  - {context.Addins.Count(addin => addin.Type == AddinType.Addin)} addins");
			markdown.AppendLine($"  - {context.Addins.Count(addin => addin.Type == AddinType.Recipe)} recipes");
			markdown.AppendLine($"  - {context.Addins.Count(addin => addin.Type == AddinType.Module)} modules");
			if (context.Addins.Any(addin => addin.Type == AddinType.Unknown))
			{
				markdown.AppendLine($"  - {context.Addins.Count(addin => addin.Type == AddinType.Unknown)} uncategorized (see the 'Exceptions' section)");
			}

			markdown.AppendLine($"- Of the {categorizedAddins.Count()} categorized packages:");
			markdown.AppendLine($"  - {auditedAddins.Count()} were successfully audited");
			markdown.AppendLine($"  - {deprecatedAddins.Count()} were marked as deprecated");
			markdown.AppendLine($"  - {exceptionAddins.Count()} could not be audited (see the 'Exceptions' section)");
			markdown.AppendLine();

			markdown.AppendLine($"- Of the {auditedAddins.Count()} audited addins:");
			markdown.AppendLine($"  - {auditedAddins.Count(addin => addin.AnalysisResult.Icon == IconAnalysisResult.RawgitUrl)} are using the cake-contrib icon on the rawgit CDN (which will be shutdown in October 2019)");
			markdown.AppendLine($"  - {auditedAddins.Count(addin => addin.AnalysisResult.Icon == IconAnalysisResult.JsDelivrUrl)} are using the cake-contrib icon on the jsDelivr CDN (which was our preferred CDN to replace rawgit)");
			markdown.AppendLine($"  - {auditedAddins.Count(addin => addin.AnalysisResult.Icon == IconAnalysisResult.CustomUrl)} are using a custom icon hosted on a web site");
			markdown.AppendLine($"  - {auditedAddins.Count(addin => addin.AnalysisResult.Icon == IconAnalysisResult.EmbeddedCakeContrib)} are embedding the cake-contrib icon (which is the current recommendation)");
			markdown.AppendLine($"  - {auditedAddins.Count(addin => addin.AnalysisResult.Icon == IconAnalysisResult.EmbeddedFancyCakeContrib)} are embedding one of the \"fancy\" cake-contrib icons (which is also recommended)");
			markdown.AppendLine($"  - {auditedAddins.Count(addin => addin.AnalysisResult.Icon == IconAnalysisResult.EmbeddedCustom)} are embedding a custom icon");
			markdown.AppendLine($"  - {auditedAddins.Count(addin => addin.AnalysisResult.TransferredToCakeContribOrganization)} have been transferred to the cake-contrib organization");
			markdown.AppendLine($"  - {auditedAddins.Count(addin => addin.AnalysisResult.ObsoleteLicenseUrlRemoved)} have replaced the obsolete `licenseUrl` with proper license metadata (see the `Additional audit results` section below for details)");
			markdown.AppendLine();

			markdown.AppendLine("## Reports");
			markdown.AppendLine();
			markdown.AppendLine($"- Click [here]({Path.GetFileNameWithoutExtension(context.MarkdownReportPath)}_for_recipes.md) to view the report for NuGet packages containing recipes.");
			foreach (var cakeVersion in Constants.CAKE_VERSIONS.OrderByDescending(v => v.Version))
			{
				markdown.AppendLine($"- Click [here]({Path.GetFileNameWithoutExtension(context.MarkdownReportPath)}_for_Cake_{cakeVersion.Version}.md) to view the report for Cake {cakeVersion.Version}.");
			}

			markdown.AppendLine();

			markdown.AppendLine("## Additional audit results");
			markdown.AppendLine();
			markdown.AppendLine("Due to space constraints we couldn't fit all audit information in this report so we generated an Excel spreadsheet that contains the following additional information:");
			markdown.AppendLine();
			markdown.AppendLine("- The `NuGet package version` column indicates the version of the package that was audited.");
			markdown.AppendLine("- The `Maintainer` column indicates who is maintaining the source for this project");
			markdown.AppendLine("- The `Icon` column indicates if the nuget package for your addin uses the cake-contrib icon.");
			markdown.AppendLine("- The `Transferred to cake-contrib` column indicates if the project has been moved to the cake-contrib github organization.");
			markdown.AppendLine("- The `License` column indicates the license selected by the addin author. PLEASE NOTE: this information is only available if the nuget package includes the new `license` metadata information (documented [here](https://docs.microsoft.com/en-us/nuget/reference/nuspec#license) and [here](https://docs.microsoft.com/en-us/nuget/reference/msbuild-targets#packing-a-license-expression-or-a-license-file)) as opposed to the [obsolete](https://github.com/NuGet/Announcements/issues/32) `licenseUrl`.");
			markdown.AppendLine("- The `Repository` column indicates if the repository information is present in the package nuspec as documented [here](https://docs.microsoft.com/en-us/nuget/reference/nuspec#repository) and [here](https://docs.microsoft.com/en-us/nuget/reference/msbuild-targets#pack-target).");
			markdown.AppendLine("- The `cake-contrib co-owner` column indicates if the cake-contrib user is a co-owner of the nuget package.");
			markdown.AppendLine("- The `Issues count` column indicates the number of open issues in the addin's github repository.");
			markdown.AppendLine("- The `Pull requests count` column indicates the number of open pull requests in the addin's github repository.");
			markdown.AppendLine("- The `Cake.Recipe` column indicates what version of Cake.Recipe is used to build this addin.");
			markdown.AppendLine("- The `Newtonsoft.Json` column indicates what version of Newtonsoft.Json is referenced by this addin (if any).");
			markdown.AppendLine("- The `Symbols` column indicates whether we found debugging symbols in the NuGet package, in the symbols package or embedded in the DLL.");
			markdown.AppendLine("- The `SourceLink` column indicates whether the SourceLink has been configured.");
			markdown.AppendLine("- The `XML Documentation` column indicates whether XML documentation is included in the nuget package.");
			markdown.AppendLine("- The `Alias Categories` column indicates the alias categories found in the addin assembly.");

			markdown.AppendLine();
			markdown.AppendLine("Click [here](Audit.xlsx) to download the Excel spreadsheet.");
			markdown.AppendLine();

			markdown.AppendLine("## Progress");
			markdown.AppendLine();
			markdown.AppendLine("The following graph shows the percentage of addins that are compatible with Cake over time. For the purpose of this graph, we consider an addin to be compatible with a given version of Cake if it references the desired version of Cake.Core and Cake.Common.");
			markdown.AppendLine();
			markdown.AppendLine($"![Progress over time]({Path.GetFileName(context.GraphSaveLocation)})");

			// Exceptions report
			markdown.AppendLine();
			markdown.Append(GenerateMarkdownWithNotes(exceptionAddins.Union(uncategorizedAddins), "Exceptions"));

			// Deprecated report
			markdown.AppendLine();
			markdown.Append(GenerateMarkdownWithNotes(deprecatedAddins, "Deprecated"));

			// Save
			await File.WriteAllTextAsync(context.MarkdownReportPath, markdown.ToString()).ConfigureAwait(false);

			// Generate the markdown report for nuget packages containing recipes
			var recipesReportName = $"{Path.GetFileNameWithoutExtension(context.MarkdownReportPath)}_for_recipes.md";
			var recipesReportPath = Path.Combine(context.TempFolder, recipesReportName);
			var markdownReportForRecipes = GenerateMarkdown(context, auditedAddins, null, AddinType.Recipe);
			await File.WriteAllTextAsync(recipesReportPath, markdownReportForRecipes).ConfigureAwait(false);

			// Generate the markdown report for each version of Cake
			foreach (var cakeVersion in Constants.CAKE_VERSIONS)
			{
				var reportName = $"{Path.GetFileNameWithoutExtension(context.MarkdownReportPath)}_for_Cake_{cakeVersion.Version}.md";
				var reportPath = Path.Combine(context.TempFolder, reportName);
				var markdownReportForCakeVersion = GenerateMarkdown(context, auditedAddins, cakeVersion, AddinType.Addin);
				await File.WriteAllTextAsync(reportPath, markdownReportForCakeVersion).ConfigureAwait(false);
			}
		}

		private static DataDestination GetMarkdownDestinationForType(AddinType type)
		{
			if (type == AddinType.Addin) return DataDestination.MarkdownForAddins;
			else if (type == AddinType.Recipe) return DataDestination.MarkdownForRecipes;
			else throw new ArgumentException($"Unable to determine the DataDestination for type {type}");
		}

		private string GenerateMarkdown(DiscoveryContext context, IEnumerable<AddinMetadata> addins, CakeVersion cakeVersion, AddinType type)
		{
			var filteredAddins = addins
				.Where(addin => string.IsNullOrEmpty(addin.AnalysisResult.Notes))
				.Where(addin => addin.Type == type)
				.ToArray();

			var reportColumns = Constants.REPORT_COLUMNS
				.Where(column => column.Destination.HasFlag(GetMarkdownDestinationForType(type)))
				.Where(column => column.ApplicableTo.HasFlag(type))
				.Select((data, index) => new { Index = index, Data = data })
				.ToArray();

			var now = DateTime.UtcNow;
			var markdown = new StringBuilder();

			if (cakeVersion != null) markdown.AppendLine($"# Audit Report for Cake {cakeVersion.Version}");
			else markdown.AppendLine("# Audit Report");

			markdown.AppendLine();
			markdown.AppendLine($"This report was generated by Cake.AddinDiscoverer {context.Version} on {now.ToLongDateString()} at {now.ToLongTimeString()} GMT");
			markdown.AppendLine();

			if (type == AddinType.Addin)
			{
				markdown.AppendLine("- The `Cake Core Version` and `Cake Common Version` columns  show the version referenced by a given addin");
				markdown.AppendLine($"- The `Cake Core IsPrivate` and `Cake Common IsPrivate` columns indicate whether the references are marked as private. In other words, we are looking for references with the `PrivateAssets=All` attribute like in this example: `<PackageReference Include=\"Cake.Common\" Version=\"{cakeVersion.Version}\" PrivateAssets=\"All\" />`");
				markdown.AppendLine($"- The `Framework` column shows the .NET framework(s) targeted by a given addin. Addins should target {cakeVersion.RequiredFramework} at a minimum, and they can also optionally multi-target {string.Concat(" or ", cakeVersion.OptionalFrameworks)}");
			}

			markdown.AppendLine();

			if (type == AddinType.Addin)
			{
				markdown.AppendLine("## Statistics");
				markdown.AppendLine();

				var addinsReferencingCakeCore = filteredAddins.Where(addin => addin.Type == AddinType.Addin & addin.AnalysisResult.CakeCoreVersion != null);
				markdown.AppendLine($"- Of the {addinsReferencingCakeCore.Count()} audited addins that reference Cake.Core:");
				markdown.AppendLine($"  - {addinsReferencingCakeCore.Count(addin => addin.AnalysisResult.CakeCoreVersion.IsUpToDate(cakeVersion.Version))} are targeting the desired version of Cake.Core");
				markdown.AppendLine($"  - {addinsReferencingCakeCore.Count(addin => addin.AnalysisResult.CakeCoreIsPrivate)} have marked the reference to Cake.Core as private");
				markdown.AppendLine();

				var addinsReferencingCakeCommon = filteredAddins.Where(addin => addin.Type == AddinType.Addin & addin.AnalysisResult.CakeCommonVersion != null);
				markdown.AppendLine($"- Of the {addinsReferencingCakeCommon.Count()} audited addins that reference Cake.Common:");
				markdown.AppendLine($"  - {addinsReferencingCakeCommon.Count(addin => addin.AnalysisResult.CakeCommonVersion.IsUpToDate(cakeVersion.Version))} are targeting the desired version of Cake.Common");
				markdown.AppendLine($"  - {addinsReferencingCakeCommon.Count(addin => addin.AnalysisResult.CakeCommonIsPrivate)} have marked the reference to Cake.Common as private");
				markdown.AppendLine();
			}

			// Title
			markdown.AppendLine("## Addins");
			markdown.AppendLine();

			// Header row 1
			foreach (var column in reportColumns)
			{
				markdown.Append($"| {column.Data.Header} ");
			}

			markdown.AppendLine("|");

			// Header row 2
			foreach (var column in reportColumns)
			{
				markdown.Append("| ");
				if (column.Data.Align == ExcelHorizontalAlignment.Center) markdown.Append(":");
				markdown.Append("---");
				if (column.Data.Align == ExcelHorizontalAlignment.Right || column.Data.Align == ExcelHorizontalAlignment.Center) markdown.Append(":");
				markdown.Append(" ");
			}

			markdown.AppendLine("|");

			// One row per addin
			foreach (var addin in filteredAddins.OrderBy(addin => addin.Name))
			{
				foreach (var column in reportColumns)
				{
					if (column.Data.ApplicableTo.HasFlag(addin.Type))
					{
						var content = column.Data.GetContent(addin);
						var hyperlink = column.Data.GetHyperLink(addin);
						var color = column.Data.GetCellColor(addin, cakeVersion);

						var emoji = string.Empty;
						if (color == Color.LightGreen) emoji = Constants.GREEN_EMOJI;
						else if (color == Color.Red) emoji = Constants.RED_EMOJI;
						else if (color == Color.Gold) emoji = Constants.YELLOW_EMOJI;

						if (hyperlink == null)
						{
							markdown.Append($"| {content} {emoji}");
						}
						else
						{
							markdown.Append($"| [{content}]({hyperlink.AbsoluteUri}) {emoji}");
						}
					}
					else
					{
						markdown.Append($"| ");
					}
				}

				markdown.AppendLine("|");
			}

			return markdown.ToString();
		}

		private string GenerateMarkdownWithNotes(IEnumerable<AddinMetadata> addins, string title)
		{
			var markdown = new StringBuilder();

			markdown.AppendLine($"## {title}");

			foreach (var addin in addins.OrderBy(p => p.Name))
			{
				markdown.AppendLine($"{Environment.NewLine}**{addin.Name}**: {addin.AnalysisResult.Notes?.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[0].Trim() ?? string.Empty}");
			}

			return markdown.ToString();
		}
	}
}
