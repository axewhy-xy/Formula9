using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Formula9.Backend
{
    // A real Singapore bus stop from LTA DataMall. It has no local position of its own: the map
    // places it through its latitude and longitude.
    public sealed class BusStop
    {
        public string Code, Name, RoadName;
        public double Latitude, Longitude;
    }

    // The backend has no imported bus data (src.transit.import_lta has not been run).
    public sealed class TransitUnavailableException : Exception
    {
        public TransitUnavailableException() : base("LTA bus data not imported") { }
    }

    // /api/transit: public reference data, so no login is needed.
    public sealed class TransitClient : BackendClientBase
    {
        public const int MaxStops = 2000;
        // The credit the data's licence requires, from the latest reply.
        public string Attribution { get; private set; }

        public TransitClient(IHttpTransport transport, Uri baseUrl = null) : base(transport, baseUrl) { }

        public async Task<List<BusStop>> GetBusStopsAsync(double south, double west, double north, double east, CancellationToken cancellation = default)
        {
            string Number(double value) => value.ToString("F6", CultureInfo.InvariantCulture);
            string reply;
            try
            {
                reply = await SendAsync(Request("GET", $"/api/transit/bus-stops?south={Number(south)}&west={Number(west)}&north={Number(north)}&east={Number(east)}&limit={MaxStops}"), cancellation);
            }
            catch (BackendErrorException error) when (error.Error.Status == 503) { throw new TransitUnavailableException(); }
            catch (NoReplyException error) when (error.Status == 503) { throw new TransitUnavailableException(); }
            return ReadStops(reply);
        }

        private List<BusStop> ReadStops(string json)
        {
            JObject document;
            try { document = JObject.Parse(json); }
            catch (JsonException error) { throw new ContractException("Invalid bus stop reply: " + error.Message); }
            Attribution = document["attribution"]?.Type == JTokenType.String ? (string)document["attribution"] : null;
            return ReadStops(document);
        }

        // Entries without a code or finite coordinates are skipped.
        private static List<BusStop> ReadStops(JObject document)
        {
            var stops = document["stops"] as JArray;
            if (stops == null) throw new ContractException("Bus stop reply has no stops");
            var result = new List<BusStop>(stops.Count);
            foreach (var stop in stops)
            {
                if (!(stop is JObject entry) || entry["code"]?.Type != JTokenType.String || !IsNumber(entry["latitude"]) || !IsNumber(entry["longitude"])) continue;
                var code = (string)entry["code"];
                var latitude = (double)entry["latitude"]; var longitude = (double)entry["longitude"];
                if (code.Length == 0 || double.IsInfinity(latitude) || double.IsNaN(latitude) || double.IsInfinity(longitude) || double.IsNaN(longitude)) continue;
                result.Add(new BusStop
                {
                    Code = code,
                    Name = entry["description"]?.Type == JTokenType.String ? (string)entry["description"] : "",
                    RoadName = entry["road_name"]?.Type == JTokenType.String ? (string)entry["road_name"] : "",
                    Latitude = latitude,
                    Longitude = longitude,
                });
            }
            return result;
        }

        private static bool IsNumber(JToken token) => token != null && (token.Type == JTokenType.Float || token.Type == JTokenType.Integer);
    }
}
