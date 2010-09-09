﻿using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using ClaySharp.Implementation;
using Orchard.ContentManagement.Handlers;
using Orchard.ContentManagement.MetaData;
using Orchard.ContentManagement.MetaData.Builders;
using Orchard.ContentManagement.MetaData.Models;
using Orchard.ContentManagement.Records;
using Orchard.Data;
using Orchard.DisplayManagement;
using Orchard.Indexing;
using Orchard.Logging;

namespace Orchard.ContentManagement {
    public class DefaultContentManager : IContentManager {
        private readonly IComponentContext _context;
        private readonly IRepository<ContentTypeRecord> _contentTypeRepository;
        private readonly IRepository<ContentItemRecord> _contentItemRepository;
        private readonly IRepository<ContentItemVersionRecord> _contentItemVersionRepository;
        private readonly IContentDefinitionManager _contentDefinitionManager;
        private readonly Func<IContentManagerSession> _contentManagerSession;
        private readonly IShapeHelperFactory _shapeHelperFactory;

        public DefaultContentManager(
            IComponentContext context,
            IRepository<ContentTypeRecord> contentTypeRepository,
            IRepository<ContentItemRecord> contentItemRepository,
            IRepository<ContentItemVersionRecord> contentItemVersionRepository,
            IContentDefinitionManager contentDefinitionManager,
            Func<IContentManagerSession> contentManagerSession,
            IShapeHelperFactory shapeHelperFactory) {
            _context = context;
            _contentTypeRepository = contentTypeRepository;
            _contentItemRepository = contentItemRepository;
            _contentItemVersionRepository = contentItemVersionRepository;
            _contentDefinitionManager = contentDefinitionManager;
            _contentManagerSession = contentManagerSession;
            _shapeHelperFactory = shapeHelperFactory;
            Logger = NullLogger.Instance;
        }

        public ILogger Logger { get; set; }

        private IEnumerable<IContentHandler> _handlers;
        public IEnumerable<IContentHandler> Handlers {
            get {
                if (_handlers == null)
                    _handlers = _context.Resolve<IEnumerable<IContentHandler>>();
                return _handlers;
            }
        }

        public IEnumerable<ContentTypeDefinition> GetContentTypeDefinitions() {
            return _contentDefinitionManager.ListTypeDefinitions();
        }

        public virtual ContentItem New(string contentType) {
            var contentTypeDefinition = _contentDefinitionManager.GetTypeDefinition(contentType);
            if (contentTypeDefinition == null) {
                contentTypeDefinition = new ContentTypeDefinitionBuilder().Named(contentType).Build();
            }

            // create a new kernel for the model instance
            var context = new ActivatingContentContext {
                ContentType = contentTypeDefinition.Name,
                Definition = contentTypeDefinition,
                Builder = new ContentItemBuilder(contentTypeDefinition)
            };

            // invoke handlers to weld aspects onto kernel
            Handlers.Invoke(handler => handler.Activating(context), Logger);

            var context2 = new ActivatedContentContext {
                ContentType = contentType,
                ContentItem = context.Builder.Build()
            };

            // back-reference for convenience (e.g. getting metadata when in a view)
            context2.ContentItem.ContentManager = this;

            Handlers.Invoke(handler => handler.Activated(context2), Logger);

            var context3 = new InitializingContentContext {
                ContentType = context2.ContentType,
                ContentItem = context2.ContentItem,
            };

            Handlers.Invoke(handler => handler.Initializing(context3), Logger);

            // composite result is returned
            return context3.ContentItem;
        }

        public virtual ContentItem Get(int id) {
            return Get(id, VersionOptions.Published);
        }

        public virtual ContentItem Get(int id, VersionOptions options) {
            var session = _contentManagerSession();
            ContentItem contentItem;

            ContentItemVersionRecord versionRecord = null;

            // obtain the root records based on version options
            if (options.VersionRecordId != 0) {
                // short-circuit if item held in session
                if (session.RecallVersionRecordId(options.VersionRecordId, out contentItem))
                    return contentItem;

                // locate explicit version record
                versionRecord = _contentItemVersionRepository.Get(options.VersionRecordId);
            }
            else {
                var record = _contentItemRepository.Get(id);
                if (record != null)
                    versionRecord = GetVersionRecord(options, record);
            }

            // no record means content item doesn't exist
            if (versionRecord == null) {
                return null;
            }

            // return item if obtained earlier in session
            if (session.RecallVersionRecordId(versionRecord.Id, out contentItem)) {
                return contentItem;
            }


            // allocate instance and set record property
            contentItem = New(versionRecord.ContentItemRecord.ContentType.Name);
            contentItem.VersionRecord = versionRecord;

            // store in session prior to loading to avoid some problems with simple circular dependencies
            session.Store(contentItem);

            // create a context with a new instance to load            
            var context = new LoadContentContext(contentItem);

            // invoke handlers to acquire state, or at least establish lazy loading callbacks
            Handlers.Invoke(handler => handler.Loading(context), Logger);
            Handlers.Invoke(handler => handler.Loaded(context), Logger);

            // when draft is required and latest is published a new version is appended 
            if (options.IsDraftRequired && versionRecord.Published) {
                return BuildNewVersion(context.ContentItem);
            }

            return context.ContentItem;
        }

        private ContentItemVersionRecord GetVersionRecord(VersionOptions options, ContentItemRecord itemRecord) {
            if (options.IsPublished) {
                return itemRecord.Versions.FirstOrDefault(
                           x => x.Published) ??
                       _contentItemVersionRepository.Get(
                           x => x.ContentItemRecord == itemRecord && x.Published);
            }
            if (options.IsLatest || options.IsDraftRequired) {
                return itemRecord.Versions.FirstOrDefault(
                           x => x.Latest) ??
                       _contentItemVersionRepository.Get(
                           x => x.ContentItemRecord == itemRecord && x.Latest);
            }
            if (options.IsDraft) {
                return itemRecord.Versions.FirstOrDefault(
                           x => x.Latest && !x.Published) ??
                       _contentItemVersionRepository.Get(
                           x => x.ContentItemRecord == itemRecord && x.Latest && !x.Published);
            }
            if (options.VersionNumber != 0) {
                return itemRecord.Versions.FirstOrDefault(
                           x => x.Number == options.VersionNumber) ??
                       _contentItemVersionRepository.Get(
                           x => x.ContentItemRecord == itemRecord && x.Number == options.VersionNumber);
            }
            return null;
        }

        public virtual IEnumerable<ContentItem> GetAllVersions(int id) {
            return _contentItemVersionRepository
                .Fetch(x => x.ContentItemRecord.Id == id)
                .OrderBy(x => x.Number)
                .Select(x => Get(x.ContentItemRecord.Id, VersionOptions.VersionRecord(x.Id)));
        }

        public virtual void Publish(ContentItem contentItem) {
            if (contentItem.VersionRecord.Published) {
                return;
            }
            // create a context for the item and it's previous published record
            var previous = contentItem.Record.Versions.SingleOrDefault(x => x.Published);
            var context = new PublishContentContext(contentItem, previous);

            // invoke handlers to acquire state, or at least establish lazy loading callbacks
            Handlers.Invoke(handler => handler.Publishing(context), Logger);

            if (previous != null) {
                previous.Published = false;
            }
            contentItem.VersionRecord.Published = true;

            Handlers.Invoke(handler => handler.Published(context), Logger);
        }

        public virtual void Unpublish(ContentItem contentItem) {
            ContentItem publishedItem;
            if (contentItem.VersionRecord.Published) {
                // the version passed in is the published one
                publishedItem = contentItem;
            }
            else {
                // try to locate the published version of this item
                publishedItem = Get(contentItem.Id, VersionOptions.Published);
            }

            if (publishedItem == null) {
                // no published version exists. no work to perform.
                return;
            }

            // create a context for the item. the publishing version is null in this case
            // and the previous version is the one active prior to unpublishing. handlers
            // should take this null check into account
            var context = new PublishContentContext(contentItem, publishedItem.VersionRecord) {
                PublishingItemVersionRecord = null
            };

            Handlers.Invoke(handler => handler.Publishing(context), Logger);

            publishedItem.VersionRecord.Published = false;

            Handlers.Invoke(handler => handler.Published(context), Logger);
        }

        public virtual void Remove(ContentItem contentItem) {
            var activeVersions = _contentItemVersionRepository.Fetch(x => x.ContentItemRecord == contentItem.Record && (x.Published || x.Latest));
            var context = new RemoveContentContext(contentItem);

            Handlers.Invoke(handler => handler.Removing(context), Logger);

            foreach (var version in activeVersions) {
                if (version.Published) {
                    version.Published = false;
                }
                if (version.Latest) {
                    version.Latest = false;
                }
            }

            Handlers.Invoke(handler => handler.Removed(context), Logger);
        }

        protected virtual ContentItem BuildNewVersion(ContentItem existingContentItem) {
            var contentItemRecord = existingContentItem.Record;

            // locate the existing and the current latest versions, allocate building version
            var existingItemVersionRecord = existingContentItem.VersionRecord;
            var buildingItemVersionRecord = new ContentItemVersionRecord {
                ContentItemRecord = contentItemRecord,
                Latest = true,
                Published = false,
                Data = existingItemVersionRecord.Data,
            };


            var latestVersion = contentItemRecord.Versions.SingleOrDefault(x => x.Latest);

            if (latestVersion != null) {
                latestVersion.Latest = false;
                buildingItemVersionRecord.Number = latestVersion.Number + 1;
            }
            else {
                buildingItemVersionRecord.Number = contentItemRecord.Versions.Max(x => x.Number) + 1;
            }

            contentItemRecord.Versions.Add(buildingItemVersionRecord);
            _contentItemVersionRepository.Create(buildingItemVersionRecord);

            var buildingContentItem = New(existingContentItem.ContentType);
            buildingContentItem.VersionRecord = buildingItemVersionRecord;

            var context = new VersionContentContext {
                Id = existingContentItem.Id,
                ContentType = existingContentItem.ContentType,
                ContentItemRecord = contentItemRecord,
                ExistingContentItem = existingContentItem,
                BuildingContentItem = buildingContentItem,
                ExistingItemVersionRecord = existingItemVersionRecord,
                BuildingItemVersionRecord = buildingItemVersionRecord,
            };
            Handlers.Invoke(handler => handler.Versioning(context), Logger);
            Handlers.Invoke(handler => handler.Versioned(context), Logger);

            return context.BuildingContentItem;
        }

        public virtual void Create(ContentItem contentItem) {
            Create(contentItem, VersionOptions.Published);
        }

        public virtual void Create(ContentItem contentItem, VersionOptions options) {
            // produce root record to determine the model id
            contentItem.VersionRecord = new ContentItemVersionRecord {
                ContentItemRecord = new ContentItemRecord {
                    ContentType = AcquireContentTypeRecord(contentItem.ContentType)
                },
                Number = 1,
                Latest = true,
                Published = true
            };
            // add to the collection manually for the created case
            contentItem.VersionRecord.ContentItemRecord.Versions.Add(contentItem.VersionRecord);

            // version may be specified
            if (options.VersionNumber != 0) {
                contentItem.VersionRecord.Number = options.VersionNumber;
            }

            // draft flag on create is required for explicitly-published content items
            if (options.IsDraft) {
                contentItem.VersionRecord.Published = false;
            }

            _contentItemRepository.Create(contentItem.Record);
            _contentItemVersionRepository.Create(contentItem.VersionRecord);


            // build a context with the initialized instance to create
            var context = new CreateContentContext(contentItem);


            // invoke handlers to add information to persistent stores
            Handlers.Invoke(handler => handler.Creating(context), Logger);
            Handlers.Invoke(handler => handler.Created(context), Logger);

            if (options.IsPublished) {
                var publishContext = new PublishContentContext(contentItem, null);

                // invoke handlers to acquire state, or at least establish lazy loading callbacks
                Handlers.Invoke(handler => handler.Publishing(publishContext), Logger);

                // invoke handlers to acquire state, or at least establish lazy loading callbacks
                Handlers.Invoke(handler => handler.Published(publishContext), Logger);
            }
        }

        public ContentItemMetadata GetItemMetadata(IContent content) {
            var context = new GetContentItemMetadataContext {
                ContentItem = content.ContentItem,
                Metadata = new ContentItemMetadata(content)
            };

            Handlers.Invoke(handler => handler.GetContentItemMetadata(context), Logger);
            //-- was - from ContentItemDriver --
            //void IContentItemDriver.GetContentItemMetadata(GetContentItemMetadataContext context) {
            //  var item = context.ContentItem.As<TContent>();
            //  if (item != null) {
            //    context.Metadata.DisplayText = GetDisplayText(item) ?? context.Metadata.DisplayText;
            //    context.Metadata.DisplayRouteValues = GetDisplayRouteValues(item) ?? context.Metadata.DisplayRouteValues;
            //    context.Metadata.EditorRouteValues = GetEditorRouteValues(item) ?? context.Metadata.EditorRouteValues;
            //    context.Metadata.CreateRouteValues = GetCreateRouteValues(item) ?? context.Metadata.CreateRouteValues;
            //  }
            //}

            return context.Metadata;
        }

        public dynamic BuildDisplayModel<TContent>(TContent content, string displayType) where TContent : IContent {
            var shapeHelper = _shapeHelperFactory.CreateHelper();
            var itemShape = shapeHelper.Items_Content(ContentItem:content.ContentItem);
            var context = new BuildDisplayModelContext(content, displayType, itemShape, _shapeHelperFactory);
            Handlers.Invoke(handler => handler.BuildDisplayShape(context), Logger);
            return context.Model;
        }

        public dynamic BuildEditorModel<TContent>(TContent content) where TContent : IContent {
            var shapeHelper = _shapeHelperFactory.CreateHelper();
            var itemShape = shapeHelper.Items_Content(ContentItem: content.ContentItem);
            var context = new BuildEditorModelContext(content, itemShape, _shapeHelperFactory);
            Handlers.Invoke(handler => handler.BuildEditorShape(context), Logger);
            return context.Model;
        }

        public dynamic UpdateEditorModel<TContent>(TContent content, IUpdateModel updater) where TContent : IContent {
            var shapeHelper = _shapeHelperFactory.CreateHelper();
            var itemShape = shapeHelper.Items_Content(ContentItem: content.ContentItem);
            var context = new UpdateEditorModelContext(content, updater, itemShape, _shapeHelperFactory);
            Handlers.Invoke(handler => handler.UpdateEditorShape(context), Logger);
            return context.Model;
        }

        public IContentQuery<ContentItem> Query() {
            var query = _context.Resolve<IContentQuery>(TypedParameter.From<IContentManager>(this));
            return query.ForPart<ContentItem>();
        }

        public void Flush() {
            _contentItemRepository.Flush();
        }

        private ContentTypeRecord AcquireContentTypeRecord(string contentType) {
            var contentTypeRecord = _contentTypeRepository.Get(x => x.Name == contentType);
            if (contentTypeRecord == null) {
                //TEMP: this is not safe... ContentItem types could be created concurrently?
                contentTypeRecord = new ContentTypeRecord { Name = contentType };
                _contentTypeRepository.Create(contentTypeRecord);
            }
            return contentTypeRecord;
        }

        public void Index(ContentItem contentItem, IDocumentIndex documentIndex) {
            var indexContentContext = new IndexContentContext(contentItem, documentIndex);

            // dispatch to handlers to retrieve index information
            Handlers.Invoke(handler => handler.Indexing(indexContentContext), Logger);

            Handlers.Invoke(handler => handler.Indexed(indexContentContext), Logger);
        }

        //public ISearchBuilder Search() {
        //    return _indexManager.HasIndexProvider() 
        //        ? _indexManager.GetSearchIndexProvider().CreateSearchBuilder("Search") 
        //        : new NullSearchBuilder();
        //}
    }
}
