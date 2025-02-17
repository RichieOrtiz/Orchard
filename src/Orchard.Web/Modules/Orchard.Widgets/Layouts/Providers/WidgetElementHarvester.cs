﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using Orchard.ContentManagement;
using Orchard.ContentManagement.Aspects;
using Orchard.ContentManagement.MetaData.Models;
using Orchard.Core.Contents.Settings;
using Orchard.Environment;
using Orchard.Environment.Extensions;
using Orchard.Layouts.Framework.Display;
using Orchard.Layouts.Framework.Drivers;
using Orchard.Layouts.Framework.Elements;
using Orchard.Layouts.Framework.Harvesters;
using Orchard.Layouts.Helpers;
using Orchard.Layouts.Models;
using Orchard.Mvc.Html;
using Orchard.Security;
using Orchard.Widgets.Layouts.Elements;
using Orchard.Widgets.ViewModels;
using ContentItem = Orchard.ContentManagement.ContentItem;

namespace Orchard.Widgets.Layouts.Providers {
    [OrchardFeature("Orchard.Widgets.Elements")]
    public class WidgetElementHarvester : Component, IElementHarvester {
        private readonly Work<IContentManager> _contentManager;
        private readonly IAuthorizer _authorizer;

        public WidgetElementHarvester(Work<IContentManager> contentManager, IAuthorizer authorizer) {
            _contentManager = contentManager;
            _authorizer = authorizer;
        }

        public IEnumerable<ElementDescriptor> HarvestElements(HarvestElementsContext context) {
            var contentTypeDefinitions = GetWidgetContentTypeDefinitions();

            return contentTypeDefinitions.Select(contentTypeDefinition => {
                var settings = contentTypeDefinition.Settings;
                var description = settings.ContainsKey("Description") ? settings["Description"] : contentTypeDefinition.DisplayName;
                return new ElementDescriptor(typeof (Widget), contentTypeDefinition.Name, T.Encode(contentTypeDefinition.DisplayName), T.Encode(description), category: "Widgets") {
                    Displaying = Displaying,
                    Editor = Editor,
                    UpdateEditor = UpdateEditor,
                    ToolboxIcon = "\uf1b2",
                    EnableEditorDialog = true,
                    Removing = RemoveContentItem,
                    Exporting = ExportElement,
                    Importing = ImportElement,
                    StateBag = new Dictionary<string, object> {
                        { "ContentTypeName", contentTypeDefinition.Name }
                    },
                    LayoutSaving = LayoutSaving
                };
            });
        }

        private void LayoutSaving(ElementSavingContext context) {
            // First, widget element container has to be stored.
            var element = (Widget)context.Element;
            if (element == null) {
                return;
            }
            var widgetId = element.WidgetId;
            var widget = _contentManager.Value.Get(widgetId.Value, VersionOptions.Latest);
            if (widget == null) {
                return;
            }

            var commonPart = widget.As<ICommonPart>();
            if (commonPart != null) {
                commonPart.Container = context.Content;
            }
        }

        private void Displaying(ElementDisplayingContext context) {
            var contentTypeName = (string)context.Element.Descriptor.StateBag["ContentTypeName"];
            var element = (Widget)context.Element;
            var widgetId = element.WidgetId;
            var versionOptions = context.DisplayType == "Design" ? VersionOptions.Latest : VersionOptions.Published;
            var widget = widgetId != null
                ? _contentManager.Value.Get(widgetId.Value, versionOptions)
                : _contentManager.Value.New(contentTypeName);

            if (!_authorizer.Authorize(Core.Contents.Permissions.ViewContent, widget)) {
                return;
            }

            var widgetShape = widget != null ? _contentManager.Value.BuildDisplay(widget, context.DisplayType) : default(dynamic);
            context.ElementShape.Widget = widget;
            context.ElementShape.WidgetShape = widgetShape;
        }

        private void Editor(ElementEditorContext context) {
            UpdateEditor(context);
        }

        private void UpdateEditor(ElementEditorContext context) {
            var contentTypeName = (string)context.Element.Descriptor.StateBag["ContentTypeName"];
            var element = (Widget) context.Element;
            var elementViewModel = new WidgetElementViewModel {
                WidgetId = element.WidgetId
            };

            if (context.Updater != null) {
                context.Updater.TryUpdateModel(elementViewModel, context.Prefix, null, null);
            }

            var widgetId = elementViewModel.WidgetId;
            var widget = widgetId != null 
                ? _contentManager.Value.Get(widgetId.Value, VersionOptions.Latest) 
                : _contentManager.Value.New(contentTypeName);

            dynamic contentEditorShape;

            if (context.Updater != null) {
                if (widget.Id == 0) {
                    _contentManager.Value.Create(widget, VersionOptions.Draft);
                }
                else {
                    var isDraftable = widget.TypeDefinition.Settings.GetModel<ContentTypeSettings>().Draftable;
                    var versionOptions = isDraftable ? VersionOptions.DraftRequired : VersionOptions.Latest;
                    widget = _contentManager.Value.Get(widget.Id, versionOptions);
                }

                element.WidgetId = widget.Id;

                // If the widget has the CommonPart attached, set its Container property to the Content (if any).
                // This helps preventing widgets from appearing as orphans.
                var commonPart = widget.As<ICommonPart>();
                if (commonPart != null)
                    commonPart.Container = context.Content;

                widget.IsPlaceableContent(true);
                contentEditorShape = _contentManager.Value.UpdateEditor(widget, context.Updater);

                _contentManager.Value.Publish(widget);
            }
            else {
                contentEditorShape = _contentManager.Value.BuildEditor(widget);
            }

            var elementEditorShape = context.ShapeFactory.EditorTemplate(TemplateName: "Elements.Widget", Model: elementViewModel, Prefix: context.Prefix);
            
            elementEditorShape.Metadata.Position = "Properties:0";
            contentEditorShape.Metadata.Position = "Properties:0";
            context.EditorResult.Add(elementEditorShape);
            context.EditorResult.Add(contentEditorShape);
        }

        private void RemoveContentItem(ElementRemovingContext context) {
            var element = (Widget) context.Element;
            var widgetId = element.WidgetId;

            // Only remove the widget if no other elements are referencing this one.
            // This can happen if the user cut an element and then pasted it back.
            // That will delete the initial element and create a copy.
            var widgetElements =
                from e in context.Elements.Flatten()
                let p = e as Widget
                where p != null && p.WidgetId == widgetId
                select p;

            if (widgetElements.Any())
                return;

            var contentItem = widgetId != null ? _contentManager.Value.Get(widgetId.Value, VersionOptions.Latest) : default(ContentItem);

            if(contentItem != null)
                _contentManager.Value.Remove(contentItem);
        }

        private void ExportElement(ExportElementContext context) {
            var element = (Widget)context.Element;
            var widgetId = element.WidgetId;
            var widget = widgetId != null ? _contentManager.Value.Get(widgetId.Value, VersionOptions.Latest) : default(ContentItem);
            var widgetIdentity = widget != null ? _contentManager.Value.GetItemMetadata(widget).Identity.ToString() : default(string);

            if (widgetIdentity != null)
                context.ExportableData["WidgetId"] = widgetIdentity;
        }

        private void ImportElement(ImportElementContext context) {
            var widgetIdentity = context.ExportableData.Get("WidgetId");

            if (String.IsNullOrWhiteSpace(widgetIdentity))
                return;

            var widget = context.Session.GetItemFromSession(widgetIdentity);

            // A new widget needs to be created and saved.
            // This is to avoid the fact the very same element ending up in multiple layouts, causing issues when e.g. deleting a LayoutWidget of a cloned ContentItem (which would delete the elements of multiple layouts).
            // The new widget is needed only when the container of the original element is different from the container of the cloned element, to ensure doing it when cloning elements and avoid doing the same when importing content.
            var cp = widget.As<ICommonPart>();
            if (cp != null) {
                var lp = cp.Container.As<LayoutPart>();
                if (lp != null && lp.Id != context.Layout.Id) {
                    widget = _contentManager.Value.Clone(widget);
                }
            }

            var element = (Widget)context.Element;

            element.WidgetId = widget != null ? widget.Id : default(int?);
        }

        private IEnumerable<ContentTypeDefinition> GetWidgetContentTypeDefinitions() {
            // Select all types that have either "the "Widget" stereotype.
            var contentTypeDefinitionsQuery =
                from contentTypeDefinition in _contentManager.Value.GetContentTypeDefinitions()
                let stereotype = contentTypeDefinition.Settings.ContainsKey("Stereotype") ? contentTypeDefinition.Settings["Stereotype"] : default(string)
                where stereotype == "Widget"
                select contentTypeDefinition;

            return contentTypeDefinitionsQuery.ToList();
        }
    }
}