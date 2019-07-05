using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Twilio;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using HtmlAgilityPack;
using System.Linq;
using Twilio.Rest.Api.V2010.Account;

namespace TeamersTurnSmsNotifier
{
    public static class Function1
    {
        [FunctionName(nameof(TimerTrigger))]
        public static async Task TimerTrigger(
           [TimerTrigger("0 0 9 * * MON")]TimerInfo myTimer,
            ExecutionContext context,
            ILogger log)
        {
            await DoTheWork(context, log);
        }

        [FunctionName(nameof(HttpTrigger))]
        public static async Task<IActionResult> HttpTrigger(
         [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
         ExecutionContext context,
         ILogger log)
        {
            await DoTheWork(context, log);
            return new OkResult();
        }

        public static async Task DoTheWork(
            ExecutionContext context,
            ILogger log)
        {
            log.LogInformation("Start SMS notification...");

            var config = new ConfigurationBuilder()
                .SetBasePath(context.FunctionAppDirectory)
                .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            var teams = config["teams"].Split(",");
            var seasons = config["seasons"].Split(",");
            var teamersUsername = config["teamersUsername"];
            var teamersPassword = config["teamersPassword"];
            var backupNumbers = config["backupNumbers"].Split(",");
            var accountSid = config["twilioAccountSid"];
            var authToken = config["twilioAuthToken"];

            TwilioClient.Init(accountSid, authToken);

            var baseAddress = new Uri("https://www.teamers.com");
            var cookieContainer = new CookieContainer();
            cookieContainer.Add(new Cookie("consent", "whatever", "/", ".teamers.com"));

            using (var handler = new HttpClientHandler() { CookieContainer = cookieContainer })
            using (var client = new HttpClient(handler) { BaseAddress = baseAddress })
            {
                var homePageResult = await client.GetAsync("/");
                homePageResult.EnsureSuccessStatusCode();

                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("username", teamersUsername),
                    new KeyValuePair<string, string>("password", teamersPassword),
                });

                var loginResult = await client.PostAsync("/login", content);
                loginResult.EnsureSuccessStatusCode();

                var playerTurns = new List<PlayerTurn>();

                foreach (var team in teams)
                    foreach (var season in seasons)
                    {
                        var teamRequest = await client.GetAsync($"/membersession?team={team}");
                        client.DefaultRequestHeaders.Referrer = new Uri("https://www.teamers.com/schedule/training");
                        var seasonRequest = await client.GetAsync($"/membersession?season={season}");
                        if (seasonRequest.StatusCode != HttpStatusCode.Redirect && seasonRequest.StatusCode != HttpStatusCode.OK)
                        {
                            continue;
                        }
                        log.LogInformation($"Start processing traings for team {team} and season {season}");

                        var tainings = await client.GetAsync("/schedule/training");

                        var tainingsDoc = new HtmlDocument();
                        tainingsDoc.LoadHtml(await tainings.Content.ReadAsStringAsync());

                        var upcomingTraining = tainingsDoc.DocumentNode.SelectSingleNode("//*[@id=\"content\"]/div[2]");
                        var upcomingTrainingId = upcomingTraining.Id;
                        //var upcomingTrainingId = "4a02dc89-0958-42cf-bc2a-7fbfce35f0f5";
                        var dateString = upcomingTraining?.SelectSingleNode("div/div[1]/div[1]")?.InnerText;
                        if (dateString == null)
                        {
                            continue;
                        }
                        var dateParts = dateString.Split(" ");

                        var day = int.Parse(dateParts[1]);
                        var year = int.Parse(dateParts[3]);
                        var month = dateParts[2];

                        var nextTrainingDateTime = DateTime.Now.Date;

                        switch (month)
                        {
                            case "januari":
                                nextTrainingDateTime = new DateTime(year, 1, day);
                                break;
                            case "februari":
                                nextTrainingDateTime = new DateTime(year, 2, day);
                                break;
                            case "maart":
                                nextTrainingDateTime = new DateTime(year, 3, day);
                                break;
                            case "april":
                                nextTrainingDateTime = new DateTime(year, 4, day);
                                break;
                            case "mei":
                                nextTrainingDateTime = new DateTime(year, 5, day);
                                break;
                            case "juni":
                                nextTrainingDateTime = new DateTime(year, 6, day);
                                break;
                            case "juli":
                                nextTrainingDateTime = new DateTime(year, 7, day);
                                break;
                            case "augustus":
                                nextTrainingDateTime = new DateTime(year, 8, day);
                                break;
                            case "september":
                                nextTrainingDateTime = new DateTime(year, 9, day);
                                break;
                            case "oktober":
                                nextTrainingDateTime = new DateTime(year, 10, day);
                                break;
                            case "november":
                                nextTrainingDateTime = new DateTime(year, 11, day);
                                break;
                            case "december":
                                nextTrainingDateTime = new DateTime(year, 12, day);
                                break;
                        }

                        var training = await client.GetAsync($"/schedule/training/{upcomingTrainingId}");
                        var trainingDoc = new HtmlDocument();
                        trainingDoc.LoadHtml(await training.Content.ReadAsStringAsync());

                        if (!client.DefaultRequestHeaders.Contains("x-requested-with"))
                        {
                            client.DefaultRequestHeaders.Add("x-requested-with", "XMLHttpRequest");
                        }
                        var events = await client.GetAsync($"/event/stats/?eventGuid={upcomingTrainingId}");
                        var eventsDoc = new HtmlDocument();
                        eventsDoc.LoadHtml(await events.Content.ReadAsStringAsync());

                        var turns2 = eventsDoc.DocumentNode.SelectNodes("//div[@class=\"details_left\"]/*[@class=\"time_place\"]/*[@class=\"turn_holder\"]");
                        foreach (var turn in turns2)
                        {
                            var title = turn.SelectSingleNode("div/a").InnerText;
                            foreach (var who in turn.SelectNodes("ul/li"))
                            {
                                var playerId = who.SelectSingleNode("a")?.Attributes["href"]?.Value?.Split("/")?.Last();
                                var playerName = who.SelectSingleNode("a/img")?.Attributes["title"]?.Value;
                                if (playerName == null || playerId == null)
                                {
                                    continue;
                                }
                                log.LogInformation($"Turn for {playerName} for {title}");

                                playerTurns.Add(new PlayerTurn
                                {
                                    TurnType = title,
                                    Id = Guid.Parse(playerId),
                                    Name = playerName,
                                    Date = nextTrainingDateTime
                                });
                            }
                        }
                        foreach (var player in playerTurns)
                        {
                            var playerRes = await client.GetAsync($"/team/{player.Id}");
                            var playerDoc = new HtmlDocument();
                            playerDoc.LoadHtml(await playerRes.Content.ReadAsStringAsync());

                            var mobile = playerDoc.DocumentNode.SelectSingleNode("//*[@id=\"TeamMember\"]/div[2]/div[3]/span[2]")?.InnerText;
                            if (mobile != null && mobile.StartsWith("06"))
                            {
                                mobile = "+316" + mobile.Substring(2);
                            }
                            player.Mobile = mobile;
                        }
                    }
                bool messageSend = false;
                log.LogInformation($"Sending message to {playerTurns.Count} players");
                foreach (var playerTurn in playerTurns)
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(playerTurn.Mobile))
                        {
                            log.LogInformation($"Sending message to {playerTurn.Name} ({playerTurn.Mobile})");

                            var message = MessageResource.Create(
                                body: $"Hoi {playerTurn.Name}, vergeet niet dat je {playerTurn.Date.Day}/{playerTurn.Date.Month} op het bierrooster staat!",
                                from: new Twilio.Types.PhoneNumber("ForzaFolley"),
                                to: new Twilio.Types.PhoneNumber(playerTurn.Mobile)
                            );
                            messageSend = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "Could not send a message!");
                    }
                }
                if (!messageSend)
                {
                    foreach (var number in backupNumbers)
                    {
                        log.LogInformation("Sending message to backup number");
                        var message = MessageResource.Create(
                            body: $"Hoi, er is geen berichtje gestuurd naar iemand op het bierrooster, wellicht even controleren?",
                            from: new Twilio.Types.PhoneNumber("ForzaFolley"),
                            to: new Twilio.Types.PhoneNumber(number)
                        );
                    }
                }
            }
        }
    }
    public class PlayerTurn
    {
        public string TurnType { get; set; }
        public string Name { get; set; }
        public string Mobile { get; set; }
        public Guid Id { get; set; }
        public DateTime Date { get; set; }
    }
}
