using System;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using System.Net;
using Microsoft.Azure.Documents;
using Microsoft.Azure.Documents.Client;
using Newtonsoft.Json;
using System.Text;
using WeatherBalloon.Messaging;
using System.Linq;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Web;
using System.Collections.Generic;
using System.IO;

namespace WeatherBalloon.Cloud
{

    public static class TrackerToPrediction
    {
        private static string cosmosUrl = Environment.GetEnvironmentVariable("CosmosUrl");
        private static string cosmosKey = Environment.GetEnvironmentVariable("CosmosKey");
        private static string cosmosDB = Environment.GetEnvironmentVariable("CosmosDB");
        private static string cosmosDoc = Environment.GetEnvironmentVariable("CosmosDoc");
        public class HabHubPredictionResponse
        {
            public string valid { get; set; }
            public string uuid { get; set; }
            public int timestamp { get; set; }
        }


        // for time conversion
        private static readonly DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static DateTime UnixTimeToDateTime(long unixTime)
        {
            return epoch.AddSeconds(unixTime);
        }
        private static HttpClient client = new HttpClient();

        [FunctionName("TrackerToPrediction")]
        public static async void Run([TimerTrigger("0 */5 * * * *")]TimerInfo myTimer, ILogger log)
        {
            log.LogInformation($"C# Timer trigger function executed at: {DateTime.Now}");

            //Configurable variables for Cosmos Query
            var minPartitionId = DateTime.Now.ToString("yyyy-MM-dd HH");
            var whereVariable = ".partitionid";
            var orderByVariable = ".TimeStamp";

            var queryBuilder = new StringBuilder();
            queryBuilder.Append("SELECT Top 1* FROM ");
            queryBuilder.Append(cosmosDoc).Append(" WHERE ").Append(cosmosDoc).Append(whereVariable).Append(" >= ").Append(@"'").Append(minPartitionId).Append(@"'");
            queryBuilder.Append(" ORDER BY ").Append(cosmosDoc).Append(orderByVariable).Append(" DESC");

            var client = new DocumentClient(new Uri(cosmosUrl), cosmosKey);

            //Get Tracker message from Cosmos
            IQueryable<TrackerMessage> getTrackerMessage =
                client.CreateDocumentQuery<TrackerMessage>(UriFactory.CreateDocumentCollectionUri(cosmosDB, cosmosDoc), queryBuilder.ToString());
            var trackerMessage = JsonConvert.DeserializeObject<TrackerMessage>(getTrackerMessage.ToString());

            //Map TrackerMessage to HabHubMessage
            HabHubMessage habHubMessage = new HabHubMessage();
            habHubMessage.alt = trackerMessage.BalloonLocation.alt;
            habHubMessage.burst = trackerMessage.BurstAltitude;
            habHubMessage.lat = trackerMessage.BalloonLocation.lat;
            habHubMessage.lon = trackerMessage.BalloonLocation.@long;

            //Habhub only has ascent so I must determine sign
            habHubMessage.ascent = trackerMessage.AveAscent;
            if (trackerMessage.AveAscent < trackerMessage.AveDescent)
                habHubMessage.ascent = trackerMessage.AveDescent * -1;

            var content = new FormUrlEncodedContent(habHubMessage.mappedDictionary);
            await postFunction(content);
        }

        private static async Task postFunction(FormUrlEncodedContent content)
        {
            var response = await client.PostAsync("http://predict.habhub.org/ajax.php?action=submitForm", content);

            if (response.IsSuccessStatusCode)
            {
                var url = buildUrl(response).Result;
                response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var csv = getSanitizedCsv(response).Result;
                    var records = getRecords(csv);

                    if (records.Count >= 2)
                    {
                        var predictionMessage = generatePredictionMessage(records);
                        //sendPrediction(predictionMessage);
                    }
                }
            }
        }


        /*         private static async void sendPrediction(PredictionMessage prediction)
                {
                    var contentType = "Prediction";

                    var messageString = JsonConvert.SerializeObject(prediction);
                    var message = new Message(Encoding.ASCII.GetBytes(messageString));
                    message.ContentType = contentType;
                    s_deviceClient = DeviceClient.CreateFromConnectionString(s_connectionString, Microsoft.Azure.Devices.Client.TransportType.Mqtt);
                    await s_deviceClient.SendEventAsync(message);
                    Console.WriteLine(message.ContentType.ToString());
                    Console.WriteLine("{0} > Sending message: {1}", DateTime.Now, messageString);
                    await Task.Delay(1000);

                } */

        private static async Task<string> buildUrl(HttpResponseMessage response)
        {
            var responseString = await response.Content.ReadAsStringAsync();

            var predictionResponse = JsonConvert.DeserializeObject<HabHubPredictionResponse>(responseString);

            // example request: http://predict.habhub.org/ajax.php?action=getCSV&uuid=202f57505788f22612beb5093b66ff53c60a43b5

            var builder = new UriBuilder("http://predict.habhub.org/ajax.php");
            builder.Port = -1;
            var query = HttpUtility.ParseQueryString(builder.Query);
            query["action"] = "getCSV";
            query["uuid"] = predictionResponse.uuid;
            builder.Query = query.ToString();
            var url = builder.ToString();

            return url;

        }

        private static async Task<string> getSanitizedCsv(HttpResponseMessage response)
        {
            var responseString = await response.Content.ReadAsStringAsync();
            var csvString = Regex.Replace(responseString, "\\\",\\\"", ",\r\n").Replace("[", "").Replace("]", "");
            var csvString2 = Regex.Replace(csvString, "\\\"", "");

            return csvString2;

        }

        private static List<PredictionMessage> getRecords(string csv)
        {
            var csvOfRecords = new CsvHelper.CsvReader(new StringReader(csv));

            csvOfRecords.Configuration.IgnoreQuotes = true;
            csvOfRecords.Configuration.HasHeaderRecord = false;
            csvOfRecords.Configuration.MissingFieldFound = null;
            csvOfRecords.Read();
            var records = new List<PredictionMessage>(csvOfRecords.GetRecords<PredictionMessage>());

            return records;
        }

        private static PredictionMessage generatePredictionMessage(List<PredictionMessage> records)
        {
            var landingRecord = records[records.Count - 2];
            var predictionMessage = new PredictionMessage();
            predictionMessage.PredictionDate = DateTime.UtcNow;
            predictionMessage.LandingDateTime = UnixTimeToDateTime(landingRecord.UnixTimestamp);
            predictionMessage.LandingLat = landingRecord.LandingLat;
            predictionMessage.LandingLong = landingRecord.LandingLong;

            return predictionMessage;
        }


    }
}
