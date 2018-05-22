﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Aggregates;
using NServiceBus;
using Infrastructure;
using Infrastructure.Extensions;
using Infrastructure.Queries;

namespace eShop.Ordering.Order
{
    public class BuyerOrders :
        IHandleQueries<Queries.BuyerOrders>,
        IHandleMessages<Events.Drafted>,
        IHandleMessages<Events.Canceled>,
        IHandleMessages<Events.Confirm>,
        IHandleMessages<Events.Paid>,
        IHandleMessages<Events.Shipped>,
        IHandleMessages<Events.AddressChanged>,
        IHandleMessages<Events.PaymentMethodChanged>,
        IHandleMessages<Entities.Item.Events.Added>,
        IHandleMessages<Entities.Item.Events.PriceOverridden>,
        IHandleMessages<Entities.Item.Events.Removed>
    {
        public async Task Handle(Queries.BuyerOrders query, IMessageHandlerContext ctx)
        {
            var builder = new QueryBuilder();
            builder.Add("UserName", query.UserName.ToString(), Operation.EQUAL);

            if (query.OrderStatus != null)
            {
                var group = builder.Grouped(Group.ANY);
                // Special case if searching for Submitted orders include WaitingValidation and StockException 
                // those are internal statuses not visible to client
                if (query.OrderStatus == Status.Submitted)
                    group.Add("Status", Status.Submitted.Value, Operation.EQUAL)
                         .Add("Status", Status.WaitingValidation.Value, Operation.EQUAL)
                         .Add("Status", Status.StockException.Value, Operation.EQUAL);
                else
                    group.Add("Status", query.OrderStatus.Value, Operation.EQUAL);
            }

            if (query.From.HasValue)
                builder.Add("Created", new DateTimeOffset(query.From.Value).ToUnixTimeMilliseconds().ToString(), Operation.GREATER_THAN_OR_EQUAL);
            if (query.To.HasValue)
                builder.Add("Created", new DateTimeOffset(query.To.Value).ToUnixTimeMilliseconds().ToString(), Operation.LESS_THAN_OR_EQUAL);

            var results = await ctx.App<Infrastructure.IUnitOfWork>().Query<Models.OrderingOrder>(builder.Build())
                .ConfigureAwait(false);

            await ctx.Result(results.Records, results.Total, results.ElapsedMs).ConfigureAwait(false);
        }


        public async Task Handle(Events.Drafted e, IMessageHandlerContext ctx)
        {
            var basket = await ctx.App<Infrastructure.IUnitOfWork>().Get<Basket.Basket.Models.BasketIndex>(e.OrderId)
                .ConfigureAwait(false);
            var buyer = await ctx.App<Infrastructure.IUnitOfWork>().Get<Buyer.Models.OrderingBuyerIndex>(e.UserName)
                .ConfigureAwait(false);

            var shipping = await ctx.App<Infrastructure.IUnitOfWork>().Get<Buyer.Entities.Address.Models.Address>(e.ShippingAddressId).ConfigureAwait(false);
            var billing = await ctx.App<Infrastructure.IUnitOfWork>().Get<Buyer.Entities.Address.Models.Address>(e.BillingAddressId).ConfigureAwait(false);
            var method = await ctx.App<Infrastructure.IUnitOfWork>().Get<Buyer.Entities.PaymentMethod.Models.PaymentMethod>(e.PaymentMethodId).ConfigureAwait(false);

            // get all items in basket
            var itemIds = await ctx.Service<Basket.Basket.Entities.Item.Services.ItemsInBasket, string[]>(x => { x.BasketId = e.BasketId; })
                .ConfigureAwait(false);

            var items = await itemIds.SelectAsync(id =>
            {
                return ctx.App<Infrastructure.IUnitOfWork>().Get<Basket.Basket.Entities.Item.Models.BasketItemIndex>(id);
            }).ConfigureAwait(false);

            var model = new Models.OrderingOrder
            {
                Id = e.OrderId,
                UserName = buyer.Id,
                BuyerName = buyer.GivenName,
                Status = Status.Submitted.Value,
                StatusDescription = Status.Submitted.Description,

                ShippingAddressId = shipping.Id,
                ShippingAddress = shipping.Street,
                ShippingCityState = $"{shipping.City}, {shipping.State}",
                ShippingZipCode = shipping.ZipCode,
                ShippingCountry = shipping.Country,

                BillingAddressId = billing.Id,
                BillingAddress = billing.Street,
                BillingCityState = $"{billing.City}, {billing.State}",
                BillingZipCode = billing.ZipCode,
                BillingCountry = billing.Country,

                PaymentMethodId = method.Id,
                PaymentMethod = Buyer.Entities.PaymentMethod.CardType.FromValue(method.CardType).Value,

                Created = e.Stamp,
                Updated = e.Stamp,
                Items = items.Select(x => new Entities.Item.Models.OrderingOrderItem
                {
                    Id = x.Id,
                    OrderId = e.OrderId,
                    ProductId =x.ProductId,
                    ProductName=x.ProductName,
                    ProductDescription=x.ProductDescription,
                    ProductPictureContents=x.ProductPictureContents,
                    ProductPictureContentType=x.ProductPictureContentType,
                    ProductPrice=x.ProductPrice,
                    Quantity=x.Quantity
                }).ToArray()
            };

            await ctx.App<Infrastructure.IUnitOfWork>().Add(e.OrderId, model).ConfigureAwait(false);
        }

        public async Task Handle(Events.Canceled e, IMessageHandlerContext ctx)
        {
            var order = await ctx.App<Infrastructure.IUnitOfWork>().Get<Models.OrderingOrder>(e.OrderId).ConfigureAwait(false);

            order.Status = Status.Cancelled.Value;
            order.StatusDescription = Status.Cancelled.Description;
            order.Updated = e.Stamp;

            await ctx.App<Infrastructure.IUnitOfWork>().Update(e.OrderId, order).ConfigureAwait(false);
        }
        public async Task Handle(Events.Confirm e, IMessageHandlerContext ctx)
        {
            var order = await ctx.App<Infrastructure.IUnitOfWork>().Get<Models.OrderingOrder>(e.OrderId).ConfigureAwait(false);

            order.Status = Status.Confirmed.Value;
            order.StatusDescription = Status.Confirmed.Description;
            order.Updated = e.Stamp;

            await ctx.App<Infrastructure.IUnitOfWork>().Update(e.OrderId, order).ConfigureAwait(false);
        }
        public async Task Handle(Events.Paid e, IMessageHandlerContext ctx)
        {
            var order = await ctx.App<Infrastructure.IUnitOfWork>().Get<Models.OrderingOrder>(e.OrderId).ConfigureAwait(false);

            order.Status = Status.Paid.Value;
            order.StatusDescription = Status.Paid.Description;
            order.Updated = e.Stamp;
            order.Paid = true;

            await ctx.App<Infrastructure.IUnitOfWork>().Update(e.OrderId, order).ConfigureAwait(false);
        }
        public async Task Handle(Events.Shipped e, IMessageHandlerContext ctx)
        {
            var order = await ctx.App<Infrastructure.IUnitOfWork>().Get<Models.OrderingOrder>(e.OrderId).ConfigureAwait(false);

            order.Status = Status.Shipped.Value;
            order.StatusDescription = Status.Shipped.Description;
            order.Updated = e.Stamp;

            await ctx.App<Infrastructure.IUnitOfWork>().Update(e.OrderId, order).ConfigureAwait(false);
        }
        public async Task Handle(Events.AddressChanged e, IMessageHandlerContext ctx)
        {
            var order = await ctx.App<Infrastructure.IUnitOfWork>().Get<Models.OrderingOrder>(e.OrderId).ConfigureAwait(false);
            var billing = await ctx.App<Infrastructure.IUnitOfWork>()
                .Get<Buyer.Entities.Address.Models.Address>(e.BillingId).ConfigureAwait(false);
            var shipping = await ctx.App<Infrastructure.IUnitOfWork>()
                .Get<Buyer.Entities.Address.Models.Address>(e.ShippingId).ConfigureAwait(false);

            order.ShippingAddressId = shipping.Id;
            order.ShippingAddress = shipping.Street;
            order.ShippingCityState = $"{shipping.City}, {shipping.Street}";
            order.ShippingZipCode = shipping.ZipCode;
            order.ShippingCountry = shipping.Country;

            order.BillingAddressId = billing.Id;
            order.BillingAddress = billing.Street;
            order.BillingCityState = $"{billing.City}, {billing.Street}";
            order.BillingZipCode = billing.ZipCode;
            order.BillingCountry = billing.Country;
            order.Updated = e.Stamp;

            await ctx.App<Infrastructure.IUnitOfWork>().Update(e.OrderId, order).ConfigureAwait(false);
        }

        public async Task Handle(Events.PaymentMethodChanged e, IMessageHandlerContext ctx)
        {
            var order = await ctx.App<Infrastructure.IUnitOfWork>().Get<Models.OrderingOrder>(e.OrderId).ConfigureAwait(false);
            var method = await ctx.App<Infrastructure.IUnitOfWork>()
                .Get<Buyer.Entities.PaymentMethod.Models.PaymentMethod>(e.PaymentMethodId).ConfigureAwait(false);

            order.PaymentMethod = Buyer.Entities.PaymentMethod.CardType.FromValue(method.CardType).Value;
            order.PaymentMethodId = method.Id;
            order.Updated = e.Stamp;

            await ctx.App<Infrastructure.IUnitOfWork>().Update(e.OrderId, order).ConfigureAwait(false);
        }
        public async Task Handle(Entities.Item.Events.Added e, IMessageHandlerContext ctx)
        {
            var order = await ctx.App<Infrastructure.IUnitOfWork>().Get<Models.OrderingOrder>(e.OrderId).ConfigureAwait(false);
            var product = await ctx.App<Infrastructure.IUnitOfWork>().Get<Catalog.Product.Models.CatalogProductIndex>(e.ProductId).ConfigureAwait(false);

            order.Items = order.Items.TryAdd(new Entities.Item.Models.OrderingOrderItem
            {
                Id = Entities.Item.Handler.ItemIdGenerator(e.OrderId, e.ProductId),
                OrderId = e.OrderId,
                ProductId = product.Id,
                ProductName = product.Name,
                ProductDescription = product.Description,
                ProductPictureContents = product.PictureContents,
                ProductPictureContentType = product.PictureContentType,
                ProductPrice = product.Price,
                Quantity = e.Quantity
            }, x => x.Id);

            order.Updated = e.Stamp;

            await ctx.App<Infrastructure.IUnitOfWork>().Update(e.OrderId, order).ConfigureAwait(false);
        }
        public async Task Handle(Entities.Item.Events.PriceOverridden e, IMessageHandlerContext ctx)
        {
            var order = await ctx.App<Infrastructure.IUnitOfWork>().Get<Models.OrderingOrder>(e.OrderId).ConfigureAwait(false);

            var item = order.Items.Single(x => x.ProductId == e.ProductId);
            item.Price = e.Price;

            order.Updated = e.Stamp;

            await ctx.App<Infrastructure.IUnitOfWork>().Update(e.OrderId, order).ConfigureAwait(false);
        }
        public async Task Handle(Entities.Item.Events.Removed e, IMessageHandlerContext ctx)
        {
            var order = await ctx.App<Infrastructure.IUnitOfWork>().Get<Models.OrderingOrder>(e.OrderId).ConfigureAwait(false);

            order.Items = order.Items.TryRemove(e.ProductId, x => x.ProductId);
            order.Updated = e.Stamp;

            await ctx.App<Infrastructure.IUnitOfWork>().Update(e.OrderId, order).ConfigureAwait(false);
        }
    }
}