﻿using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EPiServer;
using EPiServer.Commerce.Catalog.ContentTypes;
using EPiServer.Commerce.Catalog.Linking;
using EPiServer.Core;
using EPiServer.Framework.Cache;
using EPiServer.Logging;
using EPiServer.ServiceLocation;
using KachingPlugIn.Services;
using Mediachase.Commerce.Catalog;
using Mediachase.Commerce.Catalog.Events;
using Mediachase.Commerce.Engine.Events;

namespace KachingPlugIn.EventHandlers
{
    [ServiceConfiguration(Lifecycle = ServiceInstanceScope.Singleton)]
    public class CatalogContentEvents
    {
        private static readonly ILogger Logger = LogManager.GetLogger(typeof(CatalogContentEvents));
        private readonly IObjectInstanceCache _cache;
        private readonly IContentLoader _contentLoader;
        private readonly CategoryExportService _categoryExportService;
        private readonly ProductExportService _productExportService;
        private readonly ReferenceConverter _referenceConverter;
        private readonly IRelationRepository _relationRepository;

        public CatalogContentEvents(
            IObjectInstanceCache cache,
            IContentLoader contentLoader,
            CategoryExportService categoryExportService,
            ProductExportService productExportService,
            ReferenceConverter referenceConverter,
            IRelationRepository relationRepository)
        {
            _cache = cache;
            _contentLoader = contentLoader;
            _categoryExportService = categoryExportService;
            _productExportService = productExportService;
            _referenceConverter = referenceConverter;
            _relationRepository = relationRepository;
        }

        public void Initialize(
            CatalogKeyEventBroadcaster catalogKeyEventBroadcaster,
            ICatalogEvents catalogEvents,
            IContentEvents contentEvents)
        {
            catalogKeyEventBroadcaster.PriceUpdated += OnPriceUpdated;

            catalogEvents.AssociationUpdating += OnAssociationUpdated;
            catalogEvents.RelationUpdated += OnRelationUpdated;

            contentEvents.CreatedContent += OnCreatedContent;
            contentEvents.DeletingContent += OnDeletingContent;
            contentEvents.MovedContent += OnMovedContent;
            contentEvents.PublishedContent += OnPublishedContent;
        }

        public void Uninitialize(
            CatalogKeyEventBroadcaster catalogKeyEventBroadcaster,
            ICatalogEvents catalogEvents,
            IContentEvents contentEvents)
        {
            catalogKeyEventBroadcaster.PriceUpdated -= OnPriceUpdated;

            catalogEvents.AssociationUpdating -= OnAssociationUpdated;
            catalogEvents.RelationUpdated -= OnRelationUpdated;

            contentEvents.CreatedContent -= OnCreatedContent;
            contentEvents.DeletingContent -= OnDeletingContent;
            contentEvents.MovedContent -= OnMovedContent;
            contentEvents.PublishedContent -= OnPublishedContent;
        }

        private void OnAssociationUpdated(object sender, AssociationEventArgs e)
        {
            Logger.Debug("OnAssociationUpdated raised.");

            IEnumerable<ProductContent> products = GetProductsAffected(e);

            // HACK: Episerver does not clear the deleted associations from cache until after this event has completed.
            // In order to load the list of associations after deletions/updates, force delete the association list from cache.
            foreach (ProductContent entry in products)
            {
                _cache.Remove("EP:ECF:Ass:" + entry.ContentLink.ID);
            }

            _productExportService.ExportProductRecommendations(products, null);
        }

        private void OnRelationUpdated(object sender, RelationEventArgs e)
        {
            Logger.Debug("OnRelationUpdated raised.");

            // If there are changes related to catalog entries, export them.
            if (e.EntryRelationChanges.Any() || e.NodeEntryRelationChanges.Any())
            {
                // TODO
                ICollection<ProductContent> products = GetProductsAffected(e);
                foreach (ProductContent product in products)
                {
                    _productExportService.ExportProduct(product, null);
                }
            }

            if (!e.NodeRelationChanges.Any())
            {
                return;
            }

            // If there are changes between catalog nodes, export child entries and the full catalog structure.
            ICollection<NodeContent> nodes = GetNodesAffected(e);
            foreach (NodeContent node in nodes)
            {
                _productExportService.ExportChildProducts(node);
            }

            _categoryExportService.StartFullCategoryExport();
        }

        private void OnCreatedContent(object sender, ContentEventArgs e)
        {
            Logger.Debug("OnCreatedContent raised.");

            if (!(e is CopyContentEventArgs))
            {
                // Return now if the was anything other than a copy action.
                return;
            }

            if (ContentReference.IsNullOrEmpty(e.ContentLink) ||
                !_contentLoader.TryGet(e.ContentLink, CultureInfo.InvariantCulture, out CatalogContentBase catalogContentBase))
            {
                Logger.Debug("Published content is not a catalog entry.");
                return;
            }

            switch (catalogContentBase)
            {
                case EntryContentBase entryContent:
                    ICollection<ProductContent> products = GetProductsAffected(entryContent);

                    foreach (ProductContent product in products)
                    {
                        _productExportService.ExportProduct(product, null);
                    }

                    _productExportService.ExportProductAssets(products);
                    _productExportService.ExportProductRecommendations(products, null);

                    break;
                case NodeContent _:
                    // No need to export child entries on a copy/duplicate action, as there will not be any.
                    // Only re-publish the category structure.
                    _categoryExportService.StartFullCategoryExport();
                    break;
            }
        }

        private void OnDeletingContent(object sender, DeleteContentEventArgs e)
        {
            Logger.Debug("OnDeletingContent raised.");

            if (ContentReference.IsNullOrEmpty(e.ContentLink) ||
                !_contentLoader.TryGet(e.ContentLink, out CatalogContentBase catalogContentBase))
            {
                Logger.Debug("Affected content is not a catalog entry.");
                return;
            }

            switch (catalogContentBase)
            {
                case EntryContentBase entryContent:
                    ICollection<EntryContentBase> entries = GetEntriesAffected(entryContent, false, true);

                    _productExportService.DeleteProducts(entries);
                    _productExportService.DeleteProductAssets(entries);
                    _productExportService.DeleteProductRecommendations(entries);
                    break;
                case NodeContent nodeContent:
                    _productExportService.DeleteChildProducts(nodeContent);
                    _categoryExportService.StartFullCategoryExport();
                    break;
            }
        }

        private void OnMovedContent(object sender, ContentEventArgs e)
        {
            Logger.Debug("OnPublishedContent raised.");

            if (!(e.Content is CatalogContentBase catalogContentBase))
            {
                Logger.Debug("Moved content is not a catalog entry.");
                return;
            }

            switch (catalogContentBase)
            {
                case EntryContentBase entryContent:
                    ICollection<ProductContent> entries = GetProductsAffected(entryContent);
                    foreach (ProductContent productContent in entries)
                    {
                        _productExportService.ExportProduct(productContent, null);
                    }

                    break;
                case NodeContent nodeContent:
                    _productExportService.ExportChildProducts(nodeContent);
                    _categoryExportService.StartFullCategoryExport();
                    break;
            }
        }

        private void OnPublishedContent(object sender, ContentEventArgs e)
        {
            Logger.Debug("OnPublishedContent raised.");

            if (!(e.Content is CatalogContentBase catalogContentBase))
            {
                Logger.Debug("Published content is not a catalog entry.");
                return;
            }

            switch (catalogContentBase)
            {
                case EntryContentBase entryContent:
                    ICollection<ProductContent> products = GetProductsAffected(entryContent);

                    foreach (ProductContent product in products)
                    {
                        _productExportService.ExportProduct(product, null);
                    }

                    _productExportService.ExportProductAssets(products);
                    _productExportService.ExportProductRecommendations(products, null);

                    break;
                case NodeContent _:
                    _categoryExportService.StartFullCategoryExport();
                    break;
            }
        }

        private void OnPriceUpdated(object sender, PriceUpdateEventArgs e)
        {
            Logger.Debug("PriceUpdated raised.");

            var contentLinks = new HashSet<ContentReference>(
                e.CatalogKeys.Select(key => _referenceConverter.GetContentLink(key.CatalogEntryCode)));

            IEnumerable<ProductContent> products = GetProductsAffectedByPriceChanges(contentLinks);

            foreach (ProductContent productContent in products)
            {
                _productExportService.ExportProduct(productContent, null);
            }
        }

        private ICollection<NodeContent> GetNodesAffected(RelationEventArgs e)
        {
            ICollection<ContentReference> entryLinks = new HashSet<ContentReference>(ContentReferenceComparer.IgnoreVersion);

            foreach (NodeRelationChange change in e.NodeRelationChanges)
            {
                entryLinks.Add(
                    _referenceConverter.GetContentLink(change.ChildNodeId, CatalogContentType.CatalogNode, 0));
            }

            ICollection<NodeContent> entries = _contentLoader
                .GetItems(entryLinks, CultureInfo.InvariantCulture)
                .OfType<NodeContent>()
                .ToArray();

            return entries;
        }

        private ICollection<EntryContentBase> GetEntriesAffected(
            EntryContentBase entryContent,
            bool includeParentProducts,
            bool includeChildVariants)
        {
            var uniqueLinks = new HashSet<ContentReference>(ContentReferenceComparer.IgnoreVersion);

            switch (entryContent)
            {
                case VariationContent variationContent:
                    if (includeParentProducts)
                    {
                        foreach (ContentReference parentLink in _relationRepository
                            .GetParents<ProductVariation>(variationContent.ContentLink)
                            .Select(pv => pv.Parent))
                        {
                            uniqueLinks.Add(parentLink);
                        }
                    }

                    break;
                case ProductContent productContent:
                    if (includeChildVariants)
                    {
                        foreach (ContentReference childLink in _relationRepository
                        .GetParents<ProductVariation>(productContent.ContentLink)
                        .Select(pv => pv.Child))
                        {
                            uniqueLinks.Add(childLink);
                        }
                    }

                    uniqueLinks.Add(productContent.ContentLink);
                    break;
            }

            ICollection<EntryContentBase> entries = _contentLoader
                .GetItems(uniqueLinks, CultureInfo.InvariantCulture)
                .OfType<EntryContentBase>()
                .ToArray();

            return entries;
        }

        private ICollection<ProductContent> GetProductsAffected(AssociationEventArgs e)
        {
            ICollection<ContentReference> entryLinks = new HashSet<ContentReference>(ContentReferenceComparer.IgnoreVersion);

            foreach (AssociationChange change in e.Changes)
            {
                entryLinks.Add(
                    _referenceConverter.GetContentLink(change.ParentEntryId, CatalogContentType.CatalogEntry, 0));
            }

            ICollection<ProductContent> entries = _contentLoader
                .GetItems(entryLinks, CultureInfo.InvariantCulture)
                .OfType<ProductContent>()
                .ToArray();

            return entries;
        }

        private ICollection<ProductContent> GetProductsAffected(RelationEventArgs e)
        {
            ICollection<ContentReference> entryLinks = new HashSet<ContentReference>(ContentReferenceComparer.IgnoreVersion);

            foreach (EntryRelationChange change in e.EntryRelationChanges)
            {
                entryLinks.Add(
                    _referenceConverter.GetContentLink(change.ParentEntryId, CatalogContentType.CatalogEntry, 0));
            }

            foreach (NodeEntryRelationChange change in e.NodeEntryRelationChanges)
            {
                entryLinks.Add(
                    _referenceConverter.GetContentLink(change.EntryId, CatalogContentType.CatalogEntry, 0));
            }

            ICollection<ProductContent> entries = _contentLoader
                .GetItems(entryLinks, CultureInfo.InvariantCulture)
                .OfType<ProductContent>()
                .ToArray();

            return entries;
        }

        private ICollection<ProductContent> GetProductsAffected(
            EntryContentBase entryContent)
        {
            var uniqueProducts = new HashSet<ProductContent>(ContentComparer.Default);

            switch (entryContent)
            {
                case VariationContent variationContent:
                    IEnumerable<ContentReference> variantLinks = _relationRepository
                        .GetParents<ProductVariation>(variationContent.ContentLink)
                        .Select(pv => pv.Parent);

                    // Look up all parent products for this variation.
                    foreach (ProductContent parentProduct in _contentLoader
                        .GetItems(variantLinks, CultureInfo.InvariantCulture)
                        .OfType<ProductContent>())
                    {
                        uniqueProducts.Add(parentProduct);
                    }

                    break;
                case ProductContent productContent:
                    uniqueProducts.Add(productContent);
                    break;
            }

            return uniqueProducts;
        }

        private ICollection<ProductContent> GetProductsAffectedByPriceChanges(
            IEnumerable<ContentReference> contentLinks)
        {
            ICollection<ContentReference> changedLinks = new HashSet<ContentReference>(
                contentLinks,
                ContentReferenceComparer.IgnoreVersion);
            ICollection<ProductContent> products = new HashSet<ProductContent>(
                ContentComparer.Default);

            foreach (IContent content in _contentLoader.GetItems(changedLinks, CultureInfo.InvariantCulture))
            {
                switch (content)
                {
                    case VariationContent variationContent:
                        IEnumerable<ContentReference> parentProductLinks =
                            variationContent.GetParentProducts(_relationRepository);

                        foreach (var parentProduct in _contentLoader
                            .GetItems(parentProductLinks, CultureInfo.InvariantCulture)
                            .OfType<ProductContent>())
                        {
                            products.Add(parentProduct);
                        }
                        break;
                    case ProductContent productContent:
                        products.Add(_contentLoader.Get<ProductContent>(productContent.ParentLink));
                        break;

                }
            }

            return products;
        }
    }
}