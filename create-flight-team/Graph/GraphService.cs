﻿using Microsoft.Azure.WebJobs.Host;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace create_flight_team.Graph
{
    public class GraphService
    {
        private static readonly string graphEndpoint = "https://graph.microsoft.com/beta";

        private readonly string accessToken = string.Empty;
        private HttpClient httpClient = null;
        private readonly JsonSerializerSettings jsonSettings = 
            new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() };

        private TraceWriter logger = null;

        public GraphService(string accessToken, TraceWriter log = null)
        {
            this.accessToken = accessToken;
            httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);
            httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            logger = log;
        }

        public async Task<List<string>> GetUserIds(string[] pilots, string[] flightAttendants)
        {
            var userIds = new List<string>();

            // Look up each user to get their Id property
            foreach(var pilot in pilots)
            {
                var user = await GetUserByUpn(pilot);
                userIds.Add($"{graphEndpoint}/users/{user.Id}");
            }

            foreach(var flightAttendant in flightAttendants)
            {
                var user = await GetUserByUpn(flightAttendant);
                userIds.Add($"{graphEndpoint}/users/{user.Id}");
            }

            return userIds;
        }

        public async Task<User> GetMe()
        {
            var response = await MakeGraphCall(HttpMethod.Get, $"/me");
            return JsonConvert.DeserializeObject<User>(await response.Content.ReadAsStringAsync());
        }

        public async Task<User> GetUserByUpn(string upn)
        {
            var response = await MakeGraphCall(HttpMethod.Get, $"/users/{upn}");
            return JsonConvert.DeserializeObject<User>(await response.Content.ReadAsStringAsync());
        }

        public async Task<Group> CreateGroupAsync(Group group)
        {
            var response = await MakeGraphCall(HttpMethod.Post, "/groups", group);
            return JsonConvert.DeserializeObject<Group>(await response.Content.ReadAsStringAsync());
        }

        public async Task CreateTeamAsync(string groupId, Team team)
        {
            var response = await MakeGraphCall(HttpMethod.Put, $"/groups/{groupId}/team", team);
        }

        public async Task<Invitation> CreateGuestInvitationAsync(Invitation invite)
        {
            var response = await MakeGraphCall(HttpMethod.Post, "/invitations", invite);
            return JsonConvert.DeserializeObject<Invitation>(await response.Content.ReadAsStringAsync());
        }

        public async Task AddMemberAsync(string teamId, string userId, bool isOwner = false)
        {
            var addUserPayload = new AddUserToGroup() { UserPath = $"{graphEndpoint}/users/{userId}" };
            await MakeGraphCall(HttpMethod.Post, $"/groups/{teamId}/members/$ref", addUserPayload);

            // Step 3 -- Add the ID to the owners of group if requested
            if (isOwner)
            {
                await MakeGraphCall(HttpMethod.Post, $"/groups/{teamId}/owners/$ref", addUserPayload);
            }
        }

        public async Task<GraphCollection<Channel>> GetTeamChannelsAsync(string teamId)
        {
            var response = await MakeGraphCall(HttpMethod.Get, $"/teams/{teamId}/channels");
            return JsonConvert.DeserializeObject<GraphCollection<Channel>>(await response.Content.ReadAsStringAsync());
        }

        public async Task CreateChatThreadAsync(string teamId, string channelId, ChatThread thread)
        {
            var response = await MakeGraphCall(HttpMethod.Post, $"/teams/{teamId}/channels/{channelId}/chatThreads", thread);
        }

        public async Task<Channel> CreateTeamChannelAsync(string teamId, Channel channel)
        {
            var response = await MakeGraphCall(HttpMethod.Post, $"/teams/{teamId}/channels", channel);
            return JsonConvert.DeserializeObject<Channel>(await response.Content.ReadAsStringAsync());
        }

        public async Task AddAppToTeam(string teamId, TeamsApp app)
        {
            var response = await MakeGraphCall(HttpMethod.Post, $"/teams/{teamId}/apps", app);
        }

        public async Task<Site> GetSharePointSiteAsync(string sitePath)
        {
            var response = await MakeGraphCall(HttpMethod.Get, $"/sites/{sitePath}");
            return JsonConvert.DeserializeObject<Site>(await response.Content.ReadAsStringAsync());
        }

        public async Task<DriveItem> GetOneDriveItemAsync(string siteId, string itemPath)
        {
            var response = await MakeGraphCall(HttpMethod.Get, $"/sites/{siteId}/drive/{itemPath}");
            return JsonConvert.DeserializeObject<DriveItem>(await response.Content.ReadAsStringAsync());
        }

        public async Task<DriveItem> GetTeamOneDriveFolderAsync(string teamId, string folderName)
        {
            // Retry this call twice if it fails
            // There seems to be a delay between creating a Team and the drives being
            // fully created/enabled
            var response = await MakeGraphCall(HttpMethod.Get, $"/groups/{teamId}/drive/root:/{folderName}", retries: 2);
            return JsonConvert.DeserializeObject<DriveItem>(await response.Content.ReadAsStringAsync());
        }

        public async Task CopySharePointFileAsync(string siteId, string itemId, ItemReference target)
        {
            var copyPayload = new DriveItem
            {
                ParentReference = target
            };

            var response = await MakeGraphCall(HttpMethod.Post,
                $"/sites/{siteId}/drive/items/{itemId}/copy",
                copyPayload);
        }

        public async Task<Plan> CreatePlanAsync(Plan plan)
        {
            var response = await MakeGraphCall(HttpMethod.Post, $"/planner/plans", plan);
            return JsonConvert.DeserializeObject<Plan>(await response.Content.ReadAsStringAsync());
        }

        public async Task<Bucket> CreateBucketAsync(Bucket bucket)
        {
            var response = await MakeGraphCall(HttpMethod.Post, $"/planner/buckets", bucket);
            return JsonConvert.DeserializeObject<Bucket>(await response.Content.ReadAsStringAsync());
        }

        public async Task<PlannerTask> CreatePlannerTaskAsync(PlannerTask task)
        {
            var response = await MakeGraphCall(HttpMethod.Post, $"/planner/tasks", task);
            return JsonConvert.DeserializeObject<PlannerTask>(await response.Content.ReadAsStringAsync());
        }

        public async Task<Site> GetTeamSiteAsync(string teamId)
        {
            var response = await MakeGraphCall(HttpMethod.Get, $"/groups/{teamId}/sites/root");
            return JsonConvert.DeserializeObject<Site>(await response.Content.ReadAsStringAsync());
        }

        public async Task<SharePointList> CreateSharePointListAsync(string siteId, SharePointList list)
        {
            var response = await MakeGraphCall(HttpMethod.Post, $"/sites/{siteId}/lists", list);
            return JsonConvert.DeserializeObject<SharePointList>(await response.Content.ReadAsStringAsync());
        }

        private async Task<HttpResponseMessage> MakeGraphCall(HttpMethod method, string uri, object body = null, int retries = 0)
        {
            string payload = string.Empty;

            if (body != null && (method != HttpMethod.Get || method != HttpMethod.Delete))
            {
                // Serialize the body
                payload = JsonConvert.SerializeObject(body, jsonSettings);
            }

            if (logger != null)
            {
                logger.Info($"MakeGraphCall Request: {method} {uri}");
                logger.Info($"MakeGraphCall Payload: {payload}");
            }

            do
            {
                // Create the request
                var request = new HttpRequestMessage(method, $"{graphEndpoint}{uri}");
                

                if (!string.IsNullOrEmpty(payload))
                {
                    request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                }

                // Send the request
                var response = await httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    if (logger != null)
                        logger.Info($"MakeGraphCall Error: {response.StatusCode}");
                    if (retries > 0)
                    {
                        if (logger != null)
                            logger.Info($"MakeGraphCall Retrying after 2 seconds...({retries} retries remaining)");
                        Thread.Sleep(3000);
                    }
                    else
                    {
                        // No more retries, throw error
                        var error = await response.Content.ReadAsStringAsync();
                        throw new Exception(error);
                    }
                }
                else
                {
                    return response;
                }
            }
            while (retries-- > 0);

            return null;
        }
    }
}