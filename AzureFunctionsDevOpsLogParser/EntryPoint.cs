using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AzureFunctionsDevOpsLogParser
{
    public static class EntryPoint
    {
        private static HttpClient client = new HttpClient();

        static EntryPoint()
        {
            var builder = new ConfigurationBuilder()
                .AddJsonFile("local.settings.json", true)
                .AddEnvironmentVariables();

            Configuration = builder.Build();
        }

        private static IConfigurationRoot Configuration { get; }

        [FunctionName("ParseLog")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "build/builds/{buildId:int}/logs/{logId:int}")] HttpRequest req,
            int buildId,
            int logId,
            ILogger log)
        {
            log.LogInformation("function start.");

            var res = new ResponseModel();
            try
            {
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{Configuration["AzureDevOpsPat"]}")));

                // Azure DevOps より PipeLine の実行結果を取得します
                using var response = await client.GetAsync($"https://{Configuration["AzureDevOpsHost"]}/{Configuration["AzureDevOpsOrganization"]}/{Configuration["AzureDevOpsProject"]}/_apis/build/builds/{buildId}/logs/{logId}?api-version=5.1");
                response.EnsureSuccessStatusCode();

                var buildLog = await response.Content.ReadAsAsync<BuildLog>();

                // ユニットテストの結果を取得します
                var testInfo = buildLog.Value.Where(_ => _.Contains("Total tests:")).ToList();
                testInfo.ForEach(_ =>
                {
                    if (int.TryParse(Regex.Match(_, @"Total tests: (\d*)").Groups[1].Value, out var tmp)) res.TestsTotalCount += tmp;
                    if (int.TryParse(Regex.Match(_, @"Passed: (\d*)").Groups[1].Value, out tmp)) res.TestsPassedCount += tmp;
                    if (int.TryParse(Regex.Match(_, @"Failed: (\d*)").Groups[1].Value, out tmp)) res.TestsFailedCount += tmp;
                    if (int.TryParse(Regex.Match(_, @"Skipped: (\d*)").Groups[1].Value, out tmp)) res.TestsSkippedCount += tmp;
                });

                // カバレッジの結果を取得します
                var coverInfo = buildLog.Value.Where(_ => _.Contains("Lines- ") || _.Contains("Branches- ")).ToList();
                coverInfo.ForEach(_ =>
                {
                    var matches = Regex.Match(_, @"Lines- (\d*) of (\d*)");
                    if (matches.Groups.Count == 3)
                    {
                        if (decimal.TryParse(matches.Groups[1].Value, out var c1) && decimal.TryParse(matches.Groups[2].Value, out var c2)) res.LineCoverage = decimal.Parse((c1 / c2 * 100).ToString("F1"));
                    }

                    matches = Regex.Match(_, @"Branches- (\d*) of (\d*)");
                    if (matches.Groups.Count == 3)
                    {
                        if (decimal.TryParse(matches.Groups[1].Value, out var c1) && decimal.TryParse(matches.Groups[2].Value, out var c2)) res.BranchCoverage = decimal.Parse((c1 / c2 * 100).ToString("F1"));
                    }
                });

                // 静的解析の結果を取得します
                var buildInfo = buildLog.Value.Where(_ => _.Contains("Warning(s)")).ToList();
                buildInfo.ForEach(_ =>
                {
                    if (int.TryParse(Regex.Match(_, @"(\d*) Warning\(s\)").Groups[1].Value, out var tmp)) res.BuildWarningCount += tmp;
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }

            return new OkObjectResult(res);
        }
    }

    /// <summary>
    /// BuildLogです
    /// </summary>
    [DataContract]
    public class BuildLog
    {
        [DataMember(Name = "count")]
        public int Count { get; set; }

        [DataMember(Name = "value")]
        public string[] Value { get; set; }
    }

    /// <summary>
    /// ResponseModelです
    /// </summary>
    public class ResponseModel
    {
        public int TestsTotalCount { get; set; }
        public int TestsPassedCount { get; set; }
        public int TestsFailedCount { get; set; }
        public int TestsSkippedCount { get; set; }
        public decimal LineCoverage { get; set; }
        public decimal BranchCoverage { get; set; }
        public int BuildWarningCount { get; set; }
    }
}
