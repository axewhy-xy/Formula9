using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Formula9.Backend
{
    public sealed class CityBuilding
    {
        public string Id, Type, Name;
        public double HeightM;
        // Closed outlines in local metres: x east, y north of central Singapore.
        public List<List<(double X, double Y)>> Rings;
    }

    // /api/buildings: public map data, so no login is needed.
    public sealed class CityClient : BackendClientBase
    {
        public CityClient(IHttpTransport transport, Uri baseUrl = null) : base(transport, baseUrl) { }

        public async Task<List<CityBuilding>> GetBuildingsAsync(string area, CancellationToken cancellation = default)
        {
            var request = Request("GET", "/api/buildings/" + Uri.EscapeDataString(area));
            request.TimeoutSeconds = 30;
            var reply = await SendAsync(request, cancellation);
            try
            {
                return (JObject.Parse(reply)["buildings"] as JArray ?? throw new ContractException("Building reply has no buildings"))
                    .Select(b => new CityBuilding
                    {
                        Id = (string)b["id"], Type = (string)b["type"], Name = (string)b["name"], HeightM = (double)b["height_m"],
                        Rings = b["rings_m"].Select(ring => ring.Select(p => ((double)p[0], (double)p[1])).ToList()).ToList(),
                    }).ToList();
            }
            catch (Exception error) when (error is JsonException || error is ArgumentException || error is InvalidCastException || error is NullReferenceException)
            {
                throw new ContractException("Invalid building reply: " + error.Message);
            }
        }
    }
}
