using Nop.Core;
using Nop.Core.Domain.Directory;
using Nop.Core.Domain.Orders;
using Nop.Services.Catalog;
using Nop.Services.Common;
using Nop.Services.Configuration;
using Nop.Services.Directory;
using Nop.Services.Orders;
using Nop.Services.Payments;
using Nop.Services.Customers;
using System;
using System.Collections.Generic;
using System.Linq;
using Uol.PagSeguro;

namespace NopBrasil.Plugin.Payments.PagSeguro.Services
{
    public class PagSeguroService : IPagSeguroService
    {
        //todo: colocar a moeda utilizada como configuração
        private const string CURRENCY_CODE = "BRL";

        private readonly ISettingService _settingService;
        private readonly ICurrencyService _currencyService;
        private readonly CurrencySettings _currencySettings;
        private readonly PagSeguroPaymentSetting _pagSeguroPaymentSetting;
        private readonly IOrderService _orderService;
        private readonly IOrderProcessingService _orderProcessingService;
        private readonly IStoreContext _storeContext;
        private readonly ICustomerService _customerService;
        private readonly IAddressService _addressService;
        private readonly ICountryService _countryService;
        private readonly IStateProvinceService _stateProvinceService;
        private readonly IProductService _productService;

        public PagSeguroService(ISettingService settingService, ICurrencyService currencyService, CurrencySettings currencySettings, PagSeguroPaymentSetting pagSeguroPaymentSetting, IOrderService orderService, IOrderProcessingService orderProcessingService, IStoreContext storeContext, ICustomerService customerService, IAddressService addressService, ICountryService countryService, IStateProvinceService stateProvinceService, IProductService _productService)
        {
            this._settingService = settingService;
            this._currencyService = currencyService;
            this._currencySettings = currencySettings;
            this._pagSeguroPaymentSetting = pagSeguroPaymentSetting;
            this._orderService = orderService;
            this._orderProcessingService = orderProcessingService;
            this._storeContext = storeContext;
            this._customerService = customerService;
            this._addressService = addressService;
            this._countryService = countryService;
            this._stateProvinceService = stateProvinceService;
            this._productService = _productService;
        }

        public Uri CreatePayment(PostProcessPaymentRequest postProcessPaymentRequest)
        {
            // Seta as credenciais
            AccountCredentials credentials = new AccountCredentials(@_pagSeguroPaymentSetting.PagSeguroEmail, @_pagSeguroPaymentSetting.PagSeguroToken);

            PaymentRequest payment = new PaymentRequest();
            payment.Currency = CURRENCY_CODE;
            payment.Reference = postProcessPaymentRequest.Order.Id.ToString();

            LoadingItems(postProcessPaymentRequest, payment);
            LoadingShipping(postProcessPaymentRequest, payment);
            LoadingSender(postProcessPaymentRequest, payment);

            return Uol.PagSeguro.PaymentService.Register(credentials, payment);
        }

        private void LoadingSender(PostProcessPaymentRequest postProcessPaymentRequest, PaymentRequest payment)
        {
            var billingAddress = _addressService.GetAddressById(postProcessPaymentRequest.Order.BillingAddressId);
            var customer = _customerService.GetCustomerById(postProcessPaymentRequest.Order.CustomerId);

            payment.Sender = new Sender();
            payment.Sender.Email = customer.Email;
            payment.Sender.Name = $"{billingAddress.FirstName} {billingAddress.LastName}";
        }

        private decimal GetConvertedRate(decimal rate)
        {
            var usedCurrency = _currencyService.GetCurrencyByCode(CURRENCY_CODE);
            if (usedCurrency == null)
                throw new NopException($"PagSeguro payment service. Could not load \"{CURRENCY_CODE}\" currency");

            if (usedCurrency.CurrencyCode == _currencyService.GetCurrencyById(_currencySettings.PrimaryStoreCurrencyId).CurrencyCode)
                return rate;
            else
                return _currencyService.ConvertFromPrimaryStoreCurrency(rate, usedCurrency);
        }

        private void LoadingShipping(PostProcessPaymentRequest postProcessPaymentRequest, PaymentRequest payment)
        {
            payment.Shipping = new Shipping();
            payment.Shipping.ShippingType = ShippingType.NotSpecified;
            Address adress = new Address();
            adress.Complement = string.Empty;
            adress.District = string.Empty;
            adress.Number = string.Empty;
            
            if (postProcessPaymentRequest.Order.ShippingAddressId.HasValue) {
                var shippingAddress = _addressService.GetAddressById(postProcessPaymentRequest.Order.ShippingAddressId.Value);
                if (shippingAddress != null)
                {
                    adress.City = shippingAddress.City;
                    adress.Country = _countryService.GetCountryById(shippingAddress.CountryId ?? 0)?.Name ?? "*";
                    adress.PostalCode = shippingAddress.ZipPostalCode;
                    adress.State = _stateProvinceService.GetStateProvinceById(shippingAddress.StateProvinceId ?? 0)?.Abbreviation ?? "*";
                    adress.Street = shippingAddress.Address1;
                    adress.Number = ".";
                    payment.Shipping.Address = adress;
                }
            }
            payment.Shipping.Cost = Math.Round(GetConvertedRate(postProcessPaymentRequest.Order.OrderShippingInclTax), 2);
        }

        private void LoadingItems(PostProcessPaymentRequest postProcessPaymentRequest, PaymentRequest payment)
        {
            foreach (var orderItem in _orderService.GetOrderItems(postProcessPaymentRequest.Order.Id))
            {
                var product = _productService.GetProductById(orderItem.ProductId);

                Item item = new Item();
                item.Amount = Math.Round(GetConvertedRate(orderItem.UnitPriceInclTax), 2);
                item.Description = product.Name;
                item.Id = product.Id.ToString();
                item.Quantity = orderItem.Quantity;
                if (orderItem.ItemWeight.HasValue)
                    item.Weight = Convert.ToInt64(orderItem.ItemWeight);
                payment.Items.Add(item);
            }
        }

        private IEnumerable<Order> GetPendingOrders() => _orderService.SearchOrders(_storeContext.CurrentStore.Id, paymentMethodSystemName: "Payments.PagSeguro", psIds: new List<int>() { 10 }).Where(o => _orderProcessingService.CanMarkOrderAsPaid(o));

        private TransactionSummary GetTransaction(AccountCredentials credentials, string referenceCode) => TransactionSearchService.SearchByReference(credentials, referenceCode)?.Items?.FirstOrDefault();

        private bool TransactionIsPaid(TransactionSummary transaction) => (transaction?.TransactionStatus == TransactionStatus.Paid || transaction?.TransactionStatus == TransactionStatus.Available);

        public void CheckPayments()
        {
            AccountCredentials credentials = new AccountCredentials(@_pagSeguroPaymentSetting.PagSeguroEmail, @_pagSeguroPaymentSetting.PagSeguroToken);
            foreach (var order in GetPendingOrders())
                if (TransactionIsPaid(GetTransaction(credentials, order.Id.ToString())))
                    _orderProcessingService.MarkOrderAsPaid(order);
        }
    }
}