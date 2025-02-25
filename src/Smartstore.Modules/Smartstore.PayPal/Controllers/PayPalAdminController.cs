﻿using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Mvc;
using NuGet.Configuration;
using Smartstore.Caching;
using Smartstore.ComponentModel;
using Smartstore.Core.Security;
using Smartstore.Core.Stores;
using Smartstore.Engine.Modularity;
using Smartstore.PayPal.Client;
using Smartstore.PayPal.Client.Messages;
using Smartstore.Web.Controllers;
using Smartstore.Web.Modelling.Settings;
using static Smartstore.PayPal.Module;

namespace Smartstore.PayPal.Controllers
{
    [Area("Admin")]
    [Route("[area]/paypal/{action=index}/{id?}")]
    public class PayPalAdminController : ModuleController
    {
        private readonly ICacheFactory _cacheFactory;
        private readonly IProviderManager _providerManager;
        private readonly PayPalHttpClient _client;
        
        public PayPalAdminController(ICacheFactory cacheFactory, IProviderManager providerManager, PayPalHttpClient client)
        {
            _cacheFactory = cacheFactory;
            _providerManager = providerManager;
            _client = client;
        }

        [LoadSetting, AuthorizeAdmin]
        public async Task<IActionResult> Configure(PayPalSettings settings)
        {
            var model = MiniMapper.Map<PayPalSettings, ConfigurationModel>(settings);

            model.EnabledFundings = settings.EnabledFundings.SplitSafe(',').ToArray();
            model.DisabledFundings = settings.DisabledFundings.SplitSafe(',').ToArray();
            model.WebhookUrl = Url.Action(nameof(PayPalController.WebhookHandler), "PayPal", new { area = string.Empty }, "https");

            ViewBag.Provider = _providerManager.GetProvider("Payments.PayPalStandard").Metadata;

            if (settings.ClientId.HasValue() && settings.Secret.HasValue())
            {
                model.HasCredentials = true;
            }

            if (settings.WebhookId.HasValue())
            {
                model.WebHookCreated = true;
            }

            if (settings.PayerId.HasValue() && settings.ClientId.HasValue() && settings.Secret.HasValue())
            {
                try
                {
                    var getMerchantStatusRequest = new GetMerchantStatusRequest(PartnerId, settings.PayerId);
                    var getMerchantStatusResponse = await _client.ExecuteRequestAsync(getMerchantStatusRequest);
                    var merchantStatus = getMerchantStatusResponse.Body<MerchantStatus>();

                    if (!merchantStatus.PaymentsReceivable)
                    {
                        NotifyError(T("Plugins.Smartstore.PayPal.Error.PaymentsReceivable"));
                    }
                    else
                    {
                        model.PaymentsReceivable = true;
                    }

                    if (!merchantStatus.PrimaryEmailConfirmed)
                    {
                        NotifyError(T("Plugins.Smartstore.PayPal.Error.PrimaryEmailConfirmed"));
                    }
                    else
                    {
                        model.PrimaryEmailConfirmed = true;
                    }
                }
                catch (Exception ex)
                {
                    NotifyError(ex.Message);
                }
            }

            return View(model);
        }

        [HttpPost, SaveSetting, AuthorizeAdmin]
        public async Task<IActionResult> Configure(ConfigurationModel model, PayPalSettings settings)
        {
            if (!ModelState.IsValid)
            {
                return await Configure(settings);
            }

            // Clear token from cache if ClientId or Secret have changed.
            if (model.ClientId != settings.ClientId || model.Secret != settings.Secret)
            {
                await _cacheFactory.GetMemoryCache().RemoveByPatternAsync(PayPalHttpClient.PAYPAL_ACCESS_TOKEN_PATTERN_KEY);
            }

            ModelState.Clear();
            MiniMapper.Map(model, settings);

            string.Join(',', model.EnabledFundings ?? Array.Empty<string>());
            string.Join(',', model.DisabledFundings ?? Array.Empty<string>());

            return RedirectToAction(nameof(Configure));
        }

        [HttpPost]
        [FormValueRequired("createwebhook"), ActionName("Configure")]
        public async Task<IActionResult> CreateWebhook(ConfigurationModel model)
        {
            var storeScope = GetActiveStoreScopeConfiguration();
            var settings = await Services.SettingFactory.LoadSettingsAsync<PayPalSettings>(storeScope);

            if (settings.ClientId.HasValue() && settings.Secret.HasValue() && !settings.WebhookId.HasValue())
            {
                // Get Webhook ID vie API.
                try
                {
                    settings.WebhookId = await GetWebHookIdAsync();
                    await Services.SettingFactory.SaveSettingsAsync(settings, storeScope);
                }
                catch (Exception ex)
                {
                    NotifyError(ex.Message);
                }
            }

            return RedirectToAction(nameof(Configure));
        }

        /// <summary>
        /// Called by Ajax request after onboarding to get ClientId & Secret.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> GetCredentials(string authCode, string sharedId, string sellerNonce)
        {
            var success = false;
            var message = string.Empty;
            
            try
            {
                var accessTokenRequest = new AccessTokenRequest(authCode: authCode, sharedId: sharedId, sellerNonce: sellerNonce);
                var accessTokenResponse = await _client.ExecuteRequestAsync(accessTokenRequest);

                if (accessTokenResponse.Status == HttpStatusCode.OK)
                {
                    var accesstoken = accessTokenResponse.Body<AccessToken>();
                    var credentialsRequest = new GetSellerCredentialsRequest(PartnerId, accesstoken.Token);
                    var credentialsResponse = await _client.ExecuteRequestAsync(credentialsRequest);

                    if (credentialsResponse.Status == HttpStatusCode.OK)
                    {
                        var credentials = credentialsResponse.Body<SellerCredentials>();

                        var storeScope = GetActiveStoreScopeConfiguration();
                        var settings = await Services.SettingFactory.LoadSettingsAsync<PayPalSettings>(storeScope);

                        // Save settings
                        settings.ClientId = credentials.ClientId;
                        settings.Secret = credentials.ClientSecret;
                        settings.PayerId = credentials.PayerId;

                        await Services.SettingFactory.SaveSettingsAsync(settings, storeScope);

                        success = true;
                        message = T("Plugins.Smartstore.PayPal.Onboarding.Success").Value;

                        object data = new
                        {
                            clientId = credentials.ClientId,
                            clientSecret = credentials.ClientSecret,
                            payerId = credentials.PayerId,
                            success,
                            message
                        };

                        return new JsonResult(data);
                    }
                }
            }
            catch (Exception ex)
            {
                message = ex.Message;
            }

            return new JsonResult(new { success, message });
        }

        /// <summary>
        /// Called by Ajax request after onboarding to get status about confirmed email & if payments are receivable.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> GetStatus()
        {
            var success = false;
            var paymentsReceivable = false;
            var primaryEmailConfirmed = false;

            try
            {
                var storeScope = GetActiveStoreScopeConfiguration();
                var settings = await Services.SettingFactory.LoadSettingsAsync<PayPalSettings>(storeScope);

                var getMerchantStatusRequest = new GetMerchantStatusRequest(PartnerId, settings.PayerId);
                var getMerchantStatusResponse = await _client.ExecuteRequestAsync(getMerchantStatusRequest);
                var merchantStatus = getMerchantStatusResponse.Body<MerchantStatus>();

                if (merchantStatus.PaymentsReceivable)
                {
                    paymentsReceivable = true;
                }

                if (merchantStatus.PrimaryEmailConfirmed)
                {
                    primaryEmailConfirmed = true;
                }

                success = true;
            }
            catch (Exception ex)
            {
                NotifyError(ex.Message);
            }

            return new JsonResult(new { success, paymentsReceivable, primaryEmailConfirmed });
        }

        private async Task<string> GetWebHookIdAsync()
        {
            var listWebhooksResponse = await _client.ListWebhooksAsync(new ListWebhooksRequest());
            var webhooks = listWebhooksResponse.Body<Webhooks>();

            if (webhooks.Hooks.Length < 1)
            {
                // Get store URL
                var storeScope = GetActiveStoreScopeConfiguration();
                Store store;
                if (storeScope == 0)
                {
                    store = Services.StoreContext.CurrentStore;
                }
                else
                {
                    store = Services.StoreContext.GetStoreById(storeScope);
                }

                // Create webhook
                var webhook = new Webhook
                {
                    EventTypes = new EventType[]
                    {
                            new EventType { Name = "*" }
                    },
                    Url = store.GetHost(true)
                };

                var request = new CreateWebhookRequest().WithBody(webhook);
                var response = await _client.CreateWebhookAsync(request);
                webhook = response.Body<Webhook>();

                return webhook.Id;
            }
            else
            {
                // TODO: (mh) (core) Why can there be several webhooks?
                return webhooks.Hooks[0].Id;
            }
        }
    }
}