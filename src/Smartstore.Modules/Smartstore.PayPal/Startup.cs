﻿using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Smartstore.Engine;
using Smartstore.Engine.Builders;
using Smartstore.Net.Http;
using Smartstore.PayPal.Client;
using Smartstore.PayPal.Filters;
using Smartstore.Web.Controllers;

namespace Smartstore.PayPal
{
    internal class Startup : StarterBase
    {
        public override bool Matches(IApplicationContext appContext)
            => appContext.IsInstalled;

        public override void ConfigureServices(IServiceCollection services, IApplicationContext appContext)
        {
            services.Configure<MvcOptions>(o =>
            {
                o.Filters.AddConditional<MiniBasketFilter>(
                    context => context.ControllerIs<ShoppingCartController>(x => x.OffCanvasShoppingCart()));
                o.Filters.AddConditional<CheckoutFilter>(
                    context => context.ControllerIs<CheckoutController>(x => x.PaymentMethod()) && !context.HttpContext.Request.IsAjax() 
                    || context.ControllerIs<CheckoutController>(x => x.Confirm()) && !context.HttpContext.Request.IsAjax(), 200);

                o.Filters.AddConditional<FraudnetFilter>(
                    context => context.ControllerIs<PublicController>() && !context.HttpContext.Request.IsAjax());
            });

            services.AddHttpClient<PayPalHttpClient>()
                .AddSmartstoreUserAgent()
                .ConfigurePrimaryHttpMessageHandler(c => new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip
                })
                .ConfigureHttpClient(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                });
        }
    }
}
