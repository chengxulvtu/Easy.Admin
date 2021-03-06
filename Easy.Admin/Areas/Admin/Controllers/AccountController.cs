﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using Easy.Admin.Areas.Admin.Models;
using Easy.Admin.Areas.Admin.RequestParams;
using Easy.Admin.Authentication;
using Easy.Admin.Authentication.JwtBearer;
using Easy.Admin.Authentication.OAuthSignIn;
using Easy.Admin.Configuration;
using Easy.Admin.Entities;
using Easy.Admin.Filters;
using Easy.Admin.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NewLife.Log;
using NewLife.Serialization;
using XCode.Membership;

namespace Easy.Admin.Areas.Admin.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [DisplayName("账号")]
    public class AccountController : AdminControllerBase
    {
        private readonly JwtBearerAuthenticationOptions _authenticationOptions;
        private readonly OAuthConfiguration _oAuthConfiguration;
        private readonly IHttpClientFactory _clientFactory;
        private readonly IUserService _userService;

        public AccountController(IOptions<JwtBearerAuthenticationOptions> authenticationOptions,
            OAuthConfiguration oAuthConfiguration,
            IHttpClientFactory clientFactory, IUserService userService, UserManager<ApplicationUser> i)
        {
            _authenticationOptions = authenticationOptions.Value;
            _oAuthConfiguration = oAuthConfiguration;
            _clientFactory = clientFactory;
            _userService = userService;
        }

        /// <summary>
        /// 获取用户信息
        /// </summary>
        /// <returns></returns>
        [HttpGet("[action]")]
        public ApiResult GetUserInfo()
        {
            //var principal = User;
            var identity = User.Identity as IUser;

            if (identity == null)
            {
                return ApiResult.Err("用户类型错误", 500);
            }

            var data = new
            {
                identity.Avatar,
                identity.DisplayName,
                identity.ID,
                identity.Name,
                identity.RoleID,
                identity.RoleIDs,
                identity.Sex,
                Roles = identity.Roles.Select(s => new
                {
                    s.ID,
                    s.Name,
                    s.Permission
                })
            };

            return ApiResult.Ok(data);
        }

        /// <summary>
        /// 登录
        /// </summary>
        /// <param name="username"></param>
        /// <param name="password"></param>
        /// <param name="rememberMe"></param>
        /// <returns></returns>
        [HttpGet]
        [Route("Login")]
        [AllowAnonymous]
        public async Task<JwtToken> Login([FromQuery]string username, [FromQuery]string password,
            [FromQuery]bool rememberMe = false)
        {
            var result = await _userService.LoginAsync(username, password, rememberMe);

            if (result.Succeeded)
            {
                var jwtToken = HttpContext.Features.Get<JwtToken>();
                return jwtToken;
            }

            throw new ApiException(402, "用户名或密码错误");
        }

        /// <summary>
        /// 注册
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        [HttpPost("[action]")]
        [AllowAnonymous]
        public async Task<ApiResult> Register(RequestRegister model)
        {
            var result = await _userService.CreateAsync(model.UserName, model.Password);

            if (result.Succeeded)
            {
                return ApiResult.Ok();
            }

            return ApiResult.Err("注册失败");
        }

        /// <summary>
        /// 根据授权码向外部身份验证机构认证，颁发本系统token，用于swagger
        /// </summary>
        /// <returns></returns>
        [HttpPost]
        [Route("GetToken")]
        [AllowAnonymous]
        public async Task<ActionResult> GetTokenAsync([FromForm]string grant_type, [FromForm]string code, [FromForm]string client_id, [FromForm]string redirect_uri)
        {
            #region 获取token
            var tokenEndpoint = _oAuthConfiguration.Authority.EnsureEnd("/") + "connect/token";
            var requestMessage = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint);
            requestMessage.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { nameof(grant_type),grant_type },
                { nameof(code),code },
                { nameof(client_id),client_id },
                { nameof(redirect_uri),redirect_uri },
            });

            requestMessage.Headers.Add("Authorization", Request.Headers["Authorization"].ToString());

            var backchannel =  _clientFactory.CreateClient();
            var responseMessage = await backchannel.SendAsync(requestMessage);

            var contentMediaType = responseMessage.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrEmpty(contentMediaType))
            {
                XTrace.WriteLine($"Unexpected token response format. Status Code: {(int)responseMessage.StatusCode}. Content-Type header is missing.");
            }
            else if (!string.Equals(contentMediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                XTrace.WriteLine($"Unexpected token response format. Status Code: {(int)responseMessage.StatusCode}. Content-Type {responseMessage.Content.Headers.ContentType}.");
            }

            // Error handling:
            // 1. If the response body can't be parsed as json, throws.
            // 2. If the response's status code is not in 2XX range, throw OpenIdConnectProtocolException. If the body is correct parsed,
            //    pass the error information from body to the exception.
            OpenIdConnectMessage message;
            try
            {
                var responseContent = await responseMessage.Content.ReadAsStringAsync();
                message = new OpenIdConnectMessage(responseContent);
            }
            catch (Exception ex)
            {
                throw new OpenIdConnectProtocolException($"Failed to parse token response body as JSON. Status Code: {(int)responseMessage.StatusCode}. Content-Type: {responseMessage.Content.Headers.ContentType}", ex);
            }

            if (!responseMessage.IsSuccessStatusCode)
            {
                throw new ApiException(500, $"获取token失败：{message.Error}");
            }
            #endregion

            #region 解析token，颁发本系统token

            var token = new JwtSecurityTokenHandler().ReadJwtToken(message.AccessToken);
            var principal = new ClaimsPrincipal(new ClaimsIdentity(token.Claims));
            var props = new AuthenticationProperties
            {
                Items =
                {
                    {"scheme", _oAuthConfiguration.Scheme},
                }
            };

            await HttpContext.SignInAsync(OAuthSignInAuthenticationDefaults.AuthenticationScheme, principal, props);

            var jwtToken = HttpContext.Features.Get<JwtToken>();

            var tokenResult = new
            {
                access_token = jwtToken.Token.Replace(message.TokenType + " ", ""),
                expires_in = message.ExpiresIn,
                token_type = message.TokenType
            };

            return Content(tokenResult.ToJson(), "application/json");
            #endregion
        }

        /// <summary>
        /// 重定向到外部身份验证提供程序的链接
        /// </summary>
        [HttpGet]
        [Route("Challenge")]
        [AllowAnonymous]
        public Task<IActionResult> Challenge(string provider, string returnUrl)
        {
            if (string.IsNullOrEmpty(returnUrl)) returnUrl = "~/";

            // validate returnUrl - either it is a valid OIDC URL or back to a local page
            if (Url.IsLocalUrl(returnUrl) == false)
            {
                // user might have clicked on a malicious link - should be logged
                throw new Exception("invalid return URL");
            }

            // start challenge and roundtrip the return URL and scheme 
            var props = new AuthenticationProperties
            {
                RedirectUri = Url.Action(nameof(Callback)),
                //RedirectUri = returnUrl,
                Items =
                {
                    {"returnUrl", returnUrl},
                    {"scheme", provider},
                }
            };

            return Task.FromResult((IActionResult)Challenge(props, provider));
        }

        /// <summary>
        /// Post processing of external authentication
        /// </summary>
        [HttpGet]
        [Route("Callback")]
        [AllowAnonymous]
        public IActionResult Callback()
        {
            var tokenKey = _authenticationOptions.TokenKey;
            var returnUrlKey = _authenticationOptions.ReturnUrlKey;

            //// read external identity from the temporary cookie
            var token = Request.Cookies.ContainsKey(tokenKey) ? Request.Cookies[tokenKey] : null;
            var returnUrl = Request.Cookies.ContainsKey(returnUrlKey) ? Request.Cookies[returnUrlKey] : null;

            if (returnUrl != null)
            {
                returnUrl += "?token=" + token;

            }
            else
            {
                returnUrl = "~/";
            }

            Response.Cookies.Delete(returnUrlKey);

            return Redirect(returnUrl);
        }

        [HttpPost()]
        [Route("Logout")]
        public async Task<ApiResult> Logout()
        {
            await _userService.SignOutAsync();

            return ApiResult.Ok();
        }
    }
}
