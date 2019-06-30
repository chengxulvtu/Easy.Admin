﻿using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;

namespace Easy.Admin.Authentication.Github
{
    /// <summary>
    /// 参考 https://developer.github.com/apps/building-oauth-apps/authorizing-oauth-apps/
    /// </summary>
    public class GithubHandler : OAuthHandler<GithubOptions>
    {
        public GithubHandler(IOptionsMonitor<GithubOptions> options, ILoggerFactory logger,
            UrlEncoder encoder, ISystemClock clock) : base(options, logger, encoder, clock)
        {
        }

        protected override async Task<AuthenticationTicket> CreateTicketAsync(ClaimsIdentity identity,
            AuthenticationProperties properties, OAuthTokenResponse tokens)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, Options.UserInformationEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

            var response = await Backchannel.SendAsync(request, Context.RequestAborted);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"An error occurred when retrieving Github user information ({response.StatusCode}). Please check if the authentication information is correct and the corresponding Github API is enabled.");
            }

            var payload = JObject.Parse(await response.Content.ReadAsStringAsync());
            var context = new OAuthCreatingTicketContext(new ClaimsPrincipal(identity), properties, Context, Scheme,
                Options, Backchannel, tokens, payload);

            context.RunClaimActions();
            await Events.CreatingTicket(context);

            return new AuthenticationTicket(context.Principal, properties, Scheme.Name);
        }
    }
}
