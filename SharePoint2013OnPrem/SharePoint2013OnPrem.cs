﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.SharePoint.Client;
using OfficeDevPnP.Core.Framework.Provisioning.Connectors;
using OfficeDevPnP.Core.Framework.Provisioning.Model;
using OfficeDevPnP.Core.Framework.Provisioning.ObjectHandlers;
using OfficeDevPnP.Core.Framework.Provisioning.Providers.Xml;
using PnPModel = OfficeDevPnP.Core.Framework.Provisioning.Model;
using SPClient = Microsoft.SharePoint.Client;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json;

namespace Karabina.SharePoint.Provisioning
{
    public class SharePoint2013OnPrem
    {
        public SharePoint2013OnPrem()
        {
            //do nothing
        }

        private ProvisioningTemplate _editingTemplate = null;
        private ListBox _lbOutput = null;

        public ProvisioningTemplate EditingTemplate
        {
            get { return _editingTemplate; }
            set { _editingTemplate = value; }
        }

        public ListBox OutputBox
        {
            get { return _lbOutput; }
            set { _lbOutput = value; }
        }

        private void WriteMessage(string message)
        {
            _lbOutput.Items.Add(message);
            _lbOutput.TopIndex = (_lbOutput.Items.Count - 1);
            Application.DoEvents();
        }

        private void WriteMessageRange(string[] message)
        {
            _lbOutput.Items.AddRange(message);
            _lbOutput.TopIndex = (_lbOutput.Items.Count - 1);
            Application.DoEvents();
        }

        private Dictionary<string, string> GetItemFieldValues(ListItem item, ProvisioningFieldCollection fieldCollection, 
                                                              SPClient.FieldCollection fields)
        {
            Dictionary<string, string> data = new Dictionary<string, string>();
            Dictionary<string, object> fieldValues = item.FieldValues;

            foreach (ProvisioningField field in fieldCollection.Fields)
            {
                if (fieldValues.ContainsKey(field.Name))
                {
                    object value = fieldValues[field.Name];
                    if (value != null)
                    {
                        //Check what type of field we have.
                        switch (field.FieldType)
                        {
                            case ProvisioningFieldType.Lookup:
                                FieldLookup fieldLookup = fields.GetFieldByName<FieldLookup>(field.Name);
                                if (fieldLookup != null)
                                {
                                    //Check it allows multiple values
                                    if (fieldLookup.AllowMultipleValues)
                                    {
                                        //Yes, get the array of ids and values
                                        FieldLookupValue[] lookupValues = value as FieldLookupValue[];
                                        StringBuilder sb = new StringBuilder();
                                        for (int i = 0; i < lookupValues.Length; i++)
                                        {
                                            if (i > 0)
                                            {
                                                sb.Append(";#");
                                            }
                                            sb.Append($"{lookupValues[i].LookupId};#{lookupValues[i].LookupValue}");
                                        }
                                        data.Add(field.Name, sb.ToString());
                                    }
                                    else
                                    {
                                        //No, get the field id and value
                                        FieldLookupValue lookupValue = value as FieldLookupValue;
                                        data.Add(field.Name, $"{lookupValue.LookupId};#{lookupValue.LookupValue}");
                                    }
                                }
                                break;
                            case ProvisioningFieldType.User:
                                FieldUser fieldUser = fields.GetFieldByName<FieldUser>(field.Name);
                                if (fieldUser != null)
                                {
                                    //Check if it allows multiple users
                                    if (fieldUser.AllowMultipleValues)
                                    {
                                        //Yes, get the array of users
                                        FieldUserValue[] userValues = value as FieldUserValue[];
                                        StringBuilder sb = new StringBuilder();
                                        for (int i = 0; i < userValues.Length; i++)
                                        {
                                            if (i > 0)
                                            {
                                                sb.Append(";#");
                                            }
                                            sb.Append($"{userValues[i].LookupId};#{userValues[i].LookupValue}");
                                        }
                                        data.Add(field.Name, sb.ToString());
                                    }
                                    else
                                    {
                                        //No, get the user id and value
                                        FieldUserValue userValue = value as FieldUserValue;
                                        data.Add(field.Name, $"{userValue.LookupId};#{userValue.LookupValue}");
                                    }
                                }
                                break;
                            case ProvisioningFieldType.URL:
                                //Field is URL, save in url,description format.
                                FieldUrlValue urlValue = value as FieldUrlValue;
                                data.Add(field.Name, $"{urlValue.Url},{urlValue.Description}");
                                break;
                            case ProvisioningFieldType.Guid:
                                //Field is GUID, save full guid format.
                                Guid guid = Guid.Parse(value.ToString());
                                data.Add(field.Name, guid.ToString("B"));
                                break;
                            case ProvisioningFieldType.DateTime:
                                //Field is date time, save in ISO format
                                DateTime dateTime = Convert.ToDateTime(value);
                                data.Add(field.Name, dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")); //ISO format
                                break;
                            default:
                                //Field is text, number or one of the other types not checked above.
                                data.Add(field.Name, value.ToString());
                                break;
                        }
                    }
                }

            }

            return data;
        }


        private void GetItemFieldValues(ListItem item, ProvisioningFieldCollection fieldCollection, SPClient.FieldCollection fields, 
                                        Dictionary<string, string> properties)
        {
            Dictionary<string, string> data = GetItemFieldValues(item, fieldCollection, fields);
            if (data.Count > 0)
            {
                foreach (KeyValuePair<string, string> keyValue in data)
                {
                    properties.Add(keyValue.Key, keyValue.Value);
                }
            }
        }


        private void SaveListItemsToTemplate(ClientContext ctx, ListCollection lists, ListInstance listInstance)
        {
            Dictionary<string, string> values = new Dictionary<string, string>();
            List list = lists.GetByTitle(listInstance.Title);
            CamlQuery camlQuery = new CamlQuery();
            camlQuery.ViewXml = "<View/>";
            ListItemCollection listItems = list.GetItems(camlQuery);
            ctx.Load(listItems);
            SPClient.FieldCollection fields = list.Fields;
            ctx.Load(fields);
            ctx.ExecuteQuery();

            WriteMessage($"Info: Saving items from {listInstance.Title}");

            int itemCount = 0;

            if (listItems.Count > 0)
            {
                ProvisioningFieldCollection fieldCollection = new ProvisioningFieldCollection();

                //Get only the fields we need.
                foreach (Microsoft.SharePoint.Client.Field field in fields)
                {
                    if ((!field.ReadOnlyField) &&
                        (!field.Hidden) &&
                        (field.FieldTypeKind != FieldType.Attachments) &&
                        (field.FieldTypeKind != FieldType.Calculated) &&
                        (field.FieldTypeKind != FieldType.Computed) &&
                        (field.FieldTypeKind != FieldType.ContentTypeId))
                    {
                        fieldCollection.Add(field.InternalName, (ProvisioningFieldType)field.FieldTypeKind);
                    }
                }

                //Now get this items with our fields.
                foreach (ListItem item in listItems)
                {
                    itemCount++;
                    Dictionary<string, string> data = GetItemFieldValues(item, fieldCollection, fields);
                    DataRow dataRow = new DataRow(data);
                    listInstance.DataRows.Add(dataRow);
                }
            }

            WriteMessage($"Info: {itemCount} items saved");
        }

        private void FixReferenceFields(ProvisioningTemplate template, List<string> lookupLists)
        {
            WriteMessage("Info: Start performing fix up of reference fields");
            Dictionary<string, int> indexFields = new Dictionary<string, int>();
            Dictionary<string, List<string>> referenceFields = new Dictionary<string, List<string>>();
            var fields = template.SiteFields;
            int totalFields = fields.Count;
            int index = 0;
            for (index = 0; index < totalFields; index++)
            {
                PnPModel.Field field = fields[index];

                XElement fieldElement = XElement.Parse(field.SchemaXml);
                string fieldName = fieldElement.Attribute("Name").Value;

                indexFields.Add(fieldName, index);

                if (lookupLists != null)
                {
                    XAttribute listAttribute = fieldElement.Attribute("List");
                    if (listAttribute != null)
                    {
                        string lookupListTitle = listAttribute.Value.Replace("{{listid:", "").Replace("}}", "");
                        if (lookupLists.IndexOf(lookupListTitle) < 0)
                        {
                            lookupLists.Add(lookupListTitle);
                        }
                    }
                }

                if (fieldElement.HasElements)
                {
                    IEnumerable<XElement> elements = fieldElement.Elements();
                    foreach (XElement element in elements)
                    {
                        string value = string.Empty;
                        switch (element.Name.LocalName)
                        {
                            case "Default":
                                value = element.Value;
                                break;
                            case "Formula":
                                value = element.Value;
                                break;
                            case "DefaultFormula":
                                value = element.Value;
                                break;
                            default:
                                break;
                        }
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            if (value.IndexOf("{fieldtitle:") >= 0)
                            {
                                string[] values = value.Split(new char[] { '{', '}' }, StringSplitOptions.RemoveEmptyEntries);
                                foreach (string val in values)
                                {
                                    if (val.StartsWith("fieldtitle:", StringComparison.OrdinalIgnoreCase))
                                    {
                                        string fieldTitle = val.Substring(11);

                                        if (!referenceFields.ContainsKey(fieldTitle))
                                        {
                                            List<string> keyValues = new List<string>();
                                            keyValues.Add(fieldName);
                                            referenceFields.Add(fieldTitle, keyValues);
                                        }
                                        else
                                        {
                                            List<string> keyValues = referenceFields[fieldTitle];

                                            if (keyValues == null)
                                            {
                                                keyValues = new List<string>();
                                            }
                                            if (!keyValues.Contains(fieldName))
                                            {
                                                keyValues.Add(fieldName);
                                            }
                                        }

                                    }

                                }

                            }
                        }

                    }
                }
            }

            if (referenceFields.Count > 0)
            {
                WriteMessage($"Info: Found {referenceFields.Count} fields to fix up");
                foreach (var referencedField in referenceFields)
                {
                    index = indexFields[referencedField.Key];
                    if (index >= 0)
                    {
                        int lowestIndex = int.MaxValue;
                        foreach (string keyValue in referencedField.Value)
                        {
                            int keyIndex = indexFields[keyValue];
                            if (keyIndex < lowestIndex)
                            {
                                lowestIndex = keyIndex;
                            }
                        }

                        if (index > lowestIndex)
                        {
                            //Swap lowest reference field with referenced field
                            PnPModel.Field tempField = fields[lowestIndex];

                            XElement tempElement = XElement.Parse(tempField.SchemaXml);
                            string tempTitle = tempElement.Attribute("Name").Value;

                            fields[lowestIndex] = fields[index];
                            fields[index] = tempField;

                            indexFields[referencedField.Key] = lowestIndex;
                            indexFields[tempTitle] = index;
                        }
                    }

                }

            }
            WriteMessage("Info: Done performing fix up of reference fields");
        }

        private void CleanupTemplate(ProvisioningOptions provisioningOptions,
                                     ProvisioningTemplate template,
                                     ProvisioningTemplate baseTemplate)
        {
            int total = 0;
            WriteMessage($"Info: Start performing {baseTemplate.BaseSiteTemplate} template clean up");

            if (provisioningOptions.CustomActions)
            {
                if ((baseTemplate.CustomActions != null) &&
                    (template.CustomActions != null))
                {
                    if ((baseTemplate.CustomActions.SiteCustomActions != null) &&
                        (template.CustomActions.SiteCustomActions != null))
                    {
                        total = template.CustomActions.SiteCustomActions.Count;
                        WriteMessage("Cleanup: Cleaning site collection custom actions from template");
                        foreach (var customAction in baseTemplate.CustomActions.SiteCustomActions)
                        {
                            template.CustomActions.SiteCustomActions.RemoveAll(p => p.Title.Equals(customAction.Title,
                                                                                                   StringComparison.OrdinalIgnoreCase));
                        }

                        total -= template.CustomActions.SiteCustomActions.Count;
                        WriteMessage($"Cleanup: {total} site collection custom actions cleaned from template");
                    }

                    if ((baseTemplate.CustomActions.WebCustomActions != null) &&
                       (template.CustomActions.WebCustomActions != null))
                    {
                        total = template.CustomActions.WebCustomActions.Count;
                        WriteMessage("Cleanup: Cleaning site custom actions from template");
                        foreach (var customAction in baseTemplate.CustomActions.WebCustomActions)
                        {
                            template.CustomActions.WebCustomActions.RemoveAll(p => p.Title.Equals(customAction.Title,
                                                                                                  StringComparison.OrdinalIgnoreCase));
                        }

                        total -= template.CustomActions.WebCustomActions.Count;
                        WriteMessage($"Cleanup: {total} site custom actions cleaned from template");
                    }

                }

            }
            if (provisioningOptions.Features)
            {
                if ((baseTemplate.Features != null) &&
                    (template.Features != null))
                {
                    if ((baseTemplate.Features.SiteFeatures != null) &&
                        (template.Features.SiteFeatures != null))
                    {
                        total = template.Features.SiteFeatures.Count;
                        WriteMessage("Cleanup: Cleaning site collection features from template");
                        foreach (var feature in baseTemplate.Features.SiteFeatures)
                        {
                            template.Features.SiteFeatures.RemoveAll(p => (p.Id.CompareTo(feature.Id) == 0));
                        }

                        total -= template.Features.SiteFeatures.Count;
                        WriteMessage($"Cleanup: {total} site collection features cleaned from template");
                    }

                    if ((baseTemplate.Features.WebFeatures != null) &&
                        (template.Features.WebFeatures != null))
                    {
                        total = template.Features.WebFeatures.Count;
                        WriteMessage("Cleanup: Cleaning site features from template");
                        foreach (var feature in baseTemplate.Features.WebFeatures)
                        {
                            template.Features.WebFeatures.RemoveAll(p => (p.Id.CompareTo(feature.Id) == 0));
                        }

                        total -= template.Features.WebFeatures.Count;
                        WriteMessage($"Cleanup: {total} site features cleaned from template");
                    }
                }
            }
            if (provisioningOptions.Fields)
            {
                if ((baseTemplate.SiteFields != null) &&
                    (template.SiteFields != null))
                {
                    WriteMessage("Cleanup: Cleaning site collection fields from template");
                    List<string> baseFieldKeys = new List<string>();
                    Dictionary<string, int> fieldsIndex = new Dictionary<string, int>();
                    var baseFields = baseTemplate.SiteFields;
                    var fields = template.SiteFields;
                    int baseCount = baseFields.Count;
                    int count = fields.Count;
                    int totalFields = ((baseCount > count) ? baseCount : count);
                    for (int i = 0; i < totalFields; i++)
                    {
                        if (i < baseCount)
                        {
                            PnPModel.Field baseField = baseFields[i];
                            XElement baseFieldElement = XElement.Parse(baseField.SchemaXml);
                            baseFieldKeys.Add(baseFieldElement.Attribute("Name").Value);
                        }

                        if (i < count)
                        {
                            PnPModel.Field field = fields[i];
                            XElement fieldElement = XElement.Parse(field.SchemaXml);
                            fieldsIndex.Add(fieldElement.Attribute("Name").Value, i);
                        }

                    }

                    int fieldsToDelete = 0;
                    foreach (var baseFieldKey in baseFieldKeys)
                    {
                        if (fieldsIndex.ContainsKey(baseFieldKey))
                        {
                            int idx = fieldsIndex[baseFieldKey];
                            fields[idx].SchemaXml = null;
                            fieldsToDelete++;
                        }

                    }

                    if (fieldsToDelete > 0)
                    {
                        fields.RemoveAll(p => p.SchemaXml == null);
                    }

                    WriteMessage($"Cleanup: {fieldsToDelete} site collection fields cleaned from template");
                }

            }

            if (provisioningOptions.Files)
            {
                if ((baseTemplate.Files != null) &&
                    (template.Files != null))
                {
                    total = template.Files.Count;
                    WriteMessage("Cleanup: Cleaning files from template");
                    foreach (var file in baseTemplate.Files)
                    {
                        template.Files.RemoveAll(p => p.Src.Equals(file.Src, StringComparison.OrdinalIgnoreCase));
                    }

                    total -= template.Files.Count;
                    WriteMessage($"Cleanup: {total} files cleaned from template");
                }

            }

            if (provisioningOptions.ListInstances)
            {
                if ((baseTemplate.Lists != null) &&
                    (template.Lists != null))
                {
                    total = template.Lists.Count;
                    WriteMessage("Cleanup: Cleaning lists from template");
                    foreach (var listInstance in baseTemplate.Lists)
                    {
                        template.Lists.RemoveAll(p => p.Title.Equals(listInstance.Title, StringComparison.OrdinalIgnoreCase));
                    }

                    total -= template.Lists.Count;
                    WriteMessage($"Cleanup: {total} lists cleaned from template");
                }

            }

            if (provisioningOptions.Pages)
            {
                if ((baseTemplate.Pages != null) &&
                    (template.Pages != null))
                {
                    total = template.Pages.Count;
                    WriteMessage("Cleanup: Cleaning pages from template");
                    foreach (var page in baseTemplate.Pages)
                    {
                        template.Pages.RemoveAll(p => p.Url.Equals(page.Url, StringComparison.OrdinalIgnoreCase));
                    }

                    total -= template.Pages.Count;
                    WriteMessage($"Cleanup: {total} pages cleaned from template");

                }

            }

            if (provisioningOptions.Publishing)
            {
                if ((baseTemplate.Publishing != null) &&
                    (template.Publishing != null))
                {
                    if ((baseTemplate.Publishing.AvailableWebTemplates != null) &&
                        (template.Publishing.AvailableWebTemplates != null))
                    {
                        total = template.Publishing.AvailableWebTemplates.Count;
                        WriteMessage("Cleanup: Cleaning avaiable web templates from template");
                        foreach (var availableWebTemplate in baseTemplate.Publishing.AvailableWebTemplates)
                        {
                            template.Publishing.AvailableWebTemplates.RemoveAll(p => 
                                p.TemplateName.Equals(availableWebTemplate.TemplateName, StringComparison.OrdinalIgnoreCase));
                        }

                        total -= template.Publishing.AvailableWebTemplates.Count;
                        WriteMessage($"Cleanup: {total} avaiable web templates cleaned from template");
                    }

                    if ((baseTemplate.Publishing.PageLayouts != null) &&
                        (template.Publishing.PageLayouts != null))
                    {
                        total = template.Publishing.PageLayouts.Count;
                        WriteMessage("Cleanup: Cleaning page layouts from template");
                        foreach (var pageLayout in baseTemplate.Publishing.PageLayouts)
                        {
                            template.Publishing.PageLayouts.RemoveAll(p => p.Path.Equals(pageLayout.Path,
                                                                                         StringComparison.OrdinalIgnoreCase));
                        }

                        total -= template.Publishing.PageLayouts.Count;
                        WriteMessage($"Cleanup: {total} page layouts cleaned from template");
                    }

                }

            }

            if (provisioningOptions.SupportedUILanguages)
            {
                if ((baseTemplate.SupportedUILanguages != null) &&
                    (template.SupportedUILanguages != null))
                {
                    total = template.SupportedUILanguages.Count;
                    WriteMessage("Cleanup: Cleaning supported UI languages from template");
                    foreach (var supportedUILanguage in baseTemplate.SupportedUILanguages)
                    {
                        template.SupportedUILanguages.RemoveAll(p => (p.LCID == supportedUILanguage.LCID));
                    }

                    total -= template.SupportedUILanguages.Count;
                    WriteMessage($"Cleanup: {total} supported UI languages cleaned from template");

                }

            }

            if (provisioningOptions.TermGroups)
            {
                if ((baseTemplate.TermGroups != null) &&
                    (template.TermGroups != null))
                {
                    total = template.TermGroups.Count;
                    WriteMessage("Cleanup: Cleaning term groups from template");
                    foreach (var termGroup in baseTemplate.TermGroups)
                    {
                        template.TermGroups.RemoveAll(p => (p.Id.CompareTo(termGroup.Id) == 0));
                    }

                    total -= template.TermGroups.Count;
                    WriteMessage($"Cleanup: {total} term groups cleaned from template");

                }

            }

            if (provisioningOptions.Workflows)
            {
                if ((baseTemplate.Workflows != null) &&
                    (template.Workflows != null))
                {
                    if ((baseTemplate.Workflows.WorkflowSubscriptions != null) &&
                        (template.Workflows.WorkflowSubscriptions != null))
                    {
                        total = template.Workflows.WorkflowSubscriptions.Count;
                        WriteMessage("Cleanup: Cleaning workflow subscriptions from template");
                        foreach (var workflowSubscription in baseTemplate.Workflows.WorkflowSubscriptions)
                        {
                            template.Workflows.WorkflowSubscriptions.RemoveAll(p =>
                            (p.DefinitionId.CompareTo(workflowSubscription.DefinitionId) == 0));
                        }

                        total -= template.Workflows.WorkflowSubscriptions.Count;
                        WriteMessage($"Cleanup: {total} workflow subscriptions cleaned from template");

                    }


                    if ((baseTemplate.Workflows.WorkflowDefinitions != null) &&
                        (template.Workflows.WorkflowDefinitions != null))
                    {
                        total = template.Workflows.WorkflowDefinitions.Count;
                        WriteMessage("Cleanup: Cleaning workflow definitions from template");
                        foreach (var workflowDefinition in baseTemplate.Workflows.WorkflowDefinitions)
                        {
                            template.Workflows.WorkflowDefinitions.RemoveAll(p =>
                            (p.Id.CompareTo(workflowDefinition.Id) == 0));
                        }

                        total -= template.Workflows.WorkflowDefinitions.Count;
                        WriteMessage($"Cleanup: {total} workflow definitions cleaned from template");

                    }

                }

            }

            if (provisioningOptions.ContentTypes)
            {
                if ((baseTemplate.ContentTypes != null) &&
                    (template.ContentTypes != null))
                {
                    total = template.ContentTypes.Count;
                    WriteMessage("Cleanup: Cleaning content types from template");
                    foreach (var contentType in baseTemplate.ContentTypes)
                    {
                        template.ContentTypes.RemoveAll(p => p.Id.Equals(contentType.Id, StringComparison.OrdinalIgnoreCase));
                    }

                    total -= template.ContentTypes.Count;
                    WriteMessage($"Cleanup: {total} content types cleaned from template");

                }

            }

            if (provisioningOptions.PropertyBagEntries)
            {
                if ((baseTemplate.PropertyBagEntries != null) &&
                    (template.PropertyBagEntries != null))
                {
                    total = template.PropertyBagEntries.Count;
                    WriteMessage("Cleanup: Cleaning property bag entries from template");
                    foreach (var propertyBagEntry in baseTemplate.PropertyBagEntries)
                    {
                        template.PropertyBagEntries.RemoveAll(p => p.Key.Equals(propertyBagEntry.Key, 
                                                                                StringComparison.OrdinalIgnoreCase));
                    }

                    total -= template.PropertyBagEntries.Count;
                    WriteMessage($"Cleanup: {total} property bag entries cleaned from template");

                }

            }

            WriteMessage($"Info: Performed {baseTemplate.BaseSiteTemplate} template clean up");

        }

        private string TokenizeWebPartXml(Web web, string xml)
        {
            var lists = web.Lists;
            web.Context.Load(web, w => w.ServerRelativeUrl, w => w.Id);
            web.Context.Load(lists, ls => ls.Include(l => l.Id, l => l.Title));
            web.Context.ExecuteQueryRetry();

            foreach (var list in lists)
            {
                xml = Regex.Replace(xml, list.Id.ToString(), string.Format("{{listid:{0}}}", list.Title), RegexOptions.IgnoreCase);
            }
            xml = Regex.Replace(xml, web.Id.ToString(), "{siteid}", RegexOptions.IgnoreCase);
            xml = Regex.Replace(xml, "(\"" + web.ServerRelativeUrl + ")(?!&)", "\"{site}", RegexOptions.IgnoreCase);
            xml = Regex.Replace(xml, "'" + web.ServerRelativeUrl, "'{site}", RegexOptions.IgnoreCase);
            xml = Regex.Replace(xml, ">" + web.ServerRelativeUrl, ">{site}", RegexOptions.IgnoreCase);
            return xml;
        }

        private void SaveFilesToTemplate(ClientContext ctx, Web web, ListInstance listInstance, ProvisioningTemplate template)
        {
            List list = web.Lists.GetByTitle(listInstance.Title);
            CamlQuery camlQuery = new CamlQuery();
            camlQuery.ViewXml = "<View Scope='RecursiveAll'/>";
            ListItemCollection listItems = list.GetItems(camlQuery);
            ctx.Load(listItems);
            SPClient.FieldCollection fields = list.Fields;
            ctx.Load(fields);
            ctx.ExecuteQuery();

            if (listItems.Count > 0)
            {
                ProvisioningFieldCollection fieldCollection = new ProvisioningFieldCollection();

                //Get only the fields we need.
                foreach (SPClient.Field field in fields)
                {
                    if ((!field.ReadOnlyField) &&
                        (!field.Hidden) &&
                        (!field.InternalName.Equals("FileLeafRef", StringComparison.OrdinalIgnoreCase)) &&
                        (field.FieldTypeKind != FieldType.Attachments) &&
                        (field.FieldTypeKind != FieldType.Calculated) &&
                        (field.FieldTypeKind != FieldType.Computed) &&
                        (field.FieldTypeKind != FieldType.ContentTypeId) &&
                        (field.FieldTypeKind != FieldType.User) &&
                        (!field.TypeAsString.Contains("Taxonomy")))
                    {
                        fieldCollection.Add(field.InternalName, (ProvisioningFieldType)field.FieldTypeKind);
                    }
                }

                WriteMessage($"Info: Saving items from {listInstance.Title} to template");

                int itemCount = 0;

                string serverRelativeUrl = web.ServerRelativeUrl;
                string serverRelativeUrlForwardSlash = serverRelativeUrl + "/";

                foreach (ListItem item in listItems)
                {
                    itemCount++;

                    string filePathName = item["FileRef"].ToString();
                    string fileFullName = filePathName.Replace(serverRelativeUrl, "{site}");
                    string fileDirectory = item["FileDirRef"].ToString().Replace(serverRelativeUrl, "{site}");
                    string fileName = item["FileLeafRef"].ToString();
                    string fileStreamName = filePathName.Replace(serverRelativeUrlForwardSlash, "");

                    if (item.FileSystemObjectType == FileSystemObjectType.File)
                    {
                        //Make sure file is not already saved during template creation
                        int fileIndex = template.Files.FindIndex(p => 
                                            ((p.Folder.Equals(fileDirectory, StringComparison.OrdinalIgnoreCase)) &&
                                             (p.Src.Equals(fileStreamName, StringComparison.OrdinalIgnoreCase))));

                        if (fileIndex < 0)
                        {
                            SPClient.File file = item.File;
                            ctx.Load(file);
                            ctx.ExecuteQuery();

                            if (file.Exists)
                            {
                                PnPModel.File pnpFile = new PnPModel.File();
                                pnpFile.Folder = fileDirectory;
                                switch (file.Level)
                                {
                                    case SPClient.FileLevel.Draft:
                                        pnpFile.Level = PnPModel.FileLevel.Draft;
                                        break;
                                    case SPClient.FileLevel.Published:
                                        pnpFile.Level = PnPModel.FileLevel.Published;
                                        break;
                                    case SPClient.FileLevel.Checkout:
                                        pnpFile.Level = PnPModel.FileLevel.Checkout;
                                        break;
                                }
                                pnpFile.Overwrite = true;

                                pnpFile.Src = fileStreamName;

                                if (fieldCollection.Count > 0)
                                {
                                    GetItemFieldValues(item, fieldCollection, fields, pnpFile.Properties);
                                }

                                if (fileName.ToLowerInvariant().EndsWith(".aspx"))
                                {
                                    var webParts = web.GetWebParts(filePathName);
                                    foreach (var webPartDefinition in webParts)
                                    {
                                        string webPartXml = web.GetWebPartXml(webPartDefinition.Id, filePathName);
                                        var webPartxml = TokenizeWebPartXml(web, webPartXml);

                                        WebPart webPart = new WebPart()
                                        {
                                            Title = webPartDefinition.WebPart.Title,
                                            Row = (uint)webPartDefinition.WebPart.ZoneIndex,
                                            Order = (uint)webPartDefinition.WebPart.ZoneIndex,
                                            Contents = webPartxml,
                                            Zone = webPartDefinition.ZoneId
                                        };

                                        pnpFile.WebParts.Add(webPart);
                                    }

                                }

                                template.Files.Add(pnpFile);
                                FileInformation fileInfo = SPClient.File.OpenBinaryDirect(ctx, filePathName);
                                template.Connector.SaveFileStream(fileStreamName, string.Empty, fileInfo.Stream);
                            }

                        }

                    }
                    else if (item.FileSystemObjectType == FileSystemObjectType.Folder)
                    {
                        //Make sure the directory is not already stored during template creation
                        int directoryIndex = template.Directories.FindIndex(p => 
                                                ((p.Folder.Equals(fileDirectory, StringComparison.OrdinalIgnoreCase)) &&
                                                 (p.Src.Equals(fileStreamName, StringComparison.OrdinalIgnoreCase))));

                        if (directoryIndex < 0)
                        {
                            PnPModel.Directory pnpDirectory = new PnPModel.Directory();
                            pnpDirectory.Folder = fileDirectory;
                            pnpDirectory.Level = PnPModel.FileLevel.Published;
                            pnpDirectory.Overwrite = true;

                            pnpDirectory.Src = fileStreamName;

                            template.Directories.Add(pnpDirectory);
                        }

                    }

                }
                WriteMessage($"Info: {itemCount} items saved");

            }

        }

        public bool CreateProvisioningTemplate(ListBox lbOutput, ProvisioningOptions provisioningOptions)
        {
            bool result = false;
            try
            {
                _lbOutput = lbOutput;

                using (var ctx = new ClientContext(provisioningOptions.WebAddress))
                {
                    if (provisioningOptions.AuthenticationRequired)
                    {
                        if (!string.IsNullOrWhiteSpace(provisioningOptions.UserDomain))
                        {
                            ctx.Credentials = new NetworkCredential(provisioningOptions.UserNameOrEmail,
                                                                    provisioningOptions.UserPassword,
                                                                    provisioningOptions.UserDomain);

                        }
                        else
                        {
                            ctx.Credentials = new NetworkCredential(provisioningOptions.UserNameOrEmail,
                                                                    provisioningOptions.UserPassword);
                        }
                    }
                    ctx.RequestTimeout = Timeout.Infinite;

                    WriteMessage($"Connecting to {provisioningOptions.WebAddress}");

                    // Load the web with all fields we will need.
                    Web web = ctx.Web;
                    ctx.Load(web, w => w.Title,
                                  w => w.Url,
                                  w => w.WebTemplate,
                                  w => w.Configuration,
                                  w => w.AllProperties,
                                  w => w.ServerRelativeUrl);
                    ctx.ExecuteQueryRetry();

                    WriteMessage($"Creating provisioning template from {web.Title} ( {web.Url} )");
                    WriteMessage($"Base template is {web.WebTemplate}#{web.Configuration}");


                    string fileNamePNP = provisioningOptions.TemplateName + ".pnp";
                    string fileNameXML = provisioningOptions.TemplateName + ".xml";

                    ProvisioningTemplateCreationInformation ptci = new ProvisioningTemplateCreationInformation(web);

                    ptci.IncludeAllTermGroups = provisioningOptions.AllTermGroups;
                    ptci.IncludeNativePublishingFiles = provisioningOptions.NativePublishingFiles;
                    ptci.IncludeSearchConfiguration = provisioningOptions.SearchConfiguration;
                    ptci.IncludeSiteCollectionTermGroup = provisioningOptions.SiteCollectionTermGroup;
                    ptci.IncludeSiteGroups = provisioningOptions.SiteGroups;
                    ptci.IncludeTermGroupsSecurity = provisioningOptions.TermGroupsSecurity;
                    ptci.PersistBrandingFiles = provisioningOptions.BrandingFiles;
                    ptci.PersistMultiLanguageResources = provisioningOptions.MultiLanguageResources;
                    ptci.PersistPublishingFiles = provisioningOptions.PublishingFiles;

                    ptci.HandlersToProcess = (provisioningOptions.AuditSettings ? Handlers.AuditSettings : 0) |
                                             (provisioningOptions.ComposedLook ? Handlers.ComposedLook : 0) |
                                             (provisioningOptions.CustomActions ? Handlers.CustomActions : 0) |
                                             (provisioningOptions.ExtensibilityProviders ? Handlers.ExtensibilityProviders : 0) |
                                             (provisioningOptions.Features ? Handlers.Features : 0) |
                                             (provisioningOptions.Fields ? Handlers.Fields : 0) |
                                             (provisioningOptions.Files ? Handlers.Files : 0) |
                                             (provisioningOptions.ListInstances ? Handlers.Lists : 0) |
                                             (provisioningOptions.Pages ? Handlers.Pages : 0) |
                                             (provisioningOptions.Publishing ? Handlers.Publishing : 0) |
                                             (provisioningOptions.RegionalSettings ? Handlers.RegionalSettings : 0) |
                                             (provisioningOptions.SearchSettings ? Handlers.SearchSettings : 0) |
                                             (provisioningOptions.SitePolicy ? Handlers.SitePolicy : 0) |
                                             (provisioningOptions.SupportedUILanguages ? Handlers.SupportedUILanguages : 0) |
                                             (provisioningOptions.TermGroups ? Handlers.TermGroups : 0) |
                                             (provisioningOptions.Workflows ? Handlers.Workflows : 0) |
                                             (provisioningOptions.SiteSecurity ? Handlers.SiteSecurity : 0) |
                                             (provisioningOptions.ContentTypes ? Handlers.ContentTypes : 0) |
                                             (provisioningOptions.PropertyBagEntries ? Handlers.PropertyBagEntries : 0) |
                                             (provisioningOptions.PageContents ? Handlers.PageContents : 0) |
                                             (provisioningOptions.WebSettings ? Handlers.WebSettings : 0) |
                                             (provisioningOptions.Navigation ? Handlers.Navigation : 0);

                    ptci.MessagesDelegate = delegate (string message, ProvisioningMessageType messageType)
                    {
                        switch (messageType)
                        {
                            case ProvisioningMessageType.Error:
                                WriteMessage("Error: " + message);
                                break;
                            case ProvisioningMessageType.Progress:
                                WriteMessage("Progress: " + message);
                                break;
                            case ProvisioningMessageType.Warning:
                                WriteMessage("Warning: " + message);
                                break;
                            case ProvisioningMessageType.EasterEgg:
                                WriteMessage("EasterEgg: " + message);
                                break;
                            default:
                                WriteMessage("Unknown: " + message);
                                break;
                        }
                        if (!lbOutput.HorizontalScrollbar)
                        {
                            lbOutput.HorizontalScrollbar = true;
                        }
                    };

                    ptci.ProgressDelegate = delegate (string message, int progress, int total)
                    {
                        // Output progress
                        WriteMessage(string.Format("{0:00}/{1:00} - {2}", progress, total, message));
                    };

                    // Create FileSystemConnector, to be used by OpenXMLConnector
                    var fileSystemConnector = new FileSystemConnector(provisioningOptions.TemplatePath, "");

                    ptci.FileConnector = new OpenXMLConnector(fileNamePNP, fileSystemConnector, "SharePoint Team");

                    // Execute actual extraction of the tepmplate 
                    ProvisioningTemplate template = web.GetProvisioningTemplate(ptci);

                    //List to hold all the lookup list names
                    List<string> lookupListTitles = new List<string>();

                    if (provisioningOptions.Fields)
                    {
                        //fix fields with default, formula or defaultformula elements so that the referenced fields have 
                        //a lower index than the fields that reference them in the SiteFields collection
                        //This prevents the "Invalid field found" error from occuring when applying the template to a site
                        FixReferenceFields(template, lookupListTitles);
                    }

                    //Check if we should do any content operations
                    if (provisioningOptions.DocumentLibraryFiles ||
                        provisioningOptions.LookupListItems ||
                        provisioningOptions.GenericListItems ||
                        provisioningOptions.JavaScriptFiles ||
                        provisioningOptions.PublishingPages ||
                        provisioningOptions.XSLStyleSheetFiles ||
                        provisioningOptions.ImageFiles)
                    {
                        ctx.Load(web.Lists);
                        ctx.ExecuteQuery();

                        foreach (ListInstance listInstance in template.Lists)
                        {
                            string listTitle = listInstance.Title.ToLowerInvariant();
                            switch (listTitle)
                            {
                                case "documents":
                                case "site collection documents":
                                    if (provisioningOptions.DocumentLibraryFiles)
                                    {
                                        SaveFilesToTemplate(ctx, web, listInstance, template);
                                    }
                                    break;
                                case "images":
                                case "site collection images":
                                    if (provisioningOptions.ImageFiles)
                                    {
                                        SaveFilesToTemplate(ctx, web, listInstance, template);
                                    }
                                    break;
                                case "pages":
                                case "site pages":
                                    if (provisioningOptions.Pages)
                                    {
                                        SaveFilesToTemplate(ctx, web, listInstance, template);
                                    }
                                    break;
                                case "site assets":
                                    if ((provisioningOptions.DocumentLibraryFiles) ||
                                        (provisioningOptions.ImageFiles) ||
                                        (provisioningOptions.JavaScriptFiles))
                                    {
                                        SaveFilesToTemplate(ctx, web, listInstance, template);
                                    }
                                    break;
                                case "style library":
                                    if ((provisioningOptions.JavaScriptFiles) ||
                                        (provisioningOptions.XSLStyleSheetFiles) ||
                                        (provisioningOptions.ImageFiles))
                                    {
                                        SaveFilesToTemplate(ctx, web, listInstance, template);
                                    }
                                    break;
                                default:
                                    if (listInstance.TemplateType == 100) //100 = Custom list
                                    {
                                        if (provisioningOptions.GenericListItems)
                                        {
                                            SaveListItemsToTemplate(ctx, web.Lists, listInstance);
                                        }
                                        else if (provisioningOptions.LookupListItems)
                                        {
                                            if (lookupListTitles.IndexOf(listInstance.Title) >= 0)
                                            {
                                                SaveListItemsToTemplate(ctx, web.Lists, listInstance);
                                            }
                                        }
                                    }
                                    else if ((listInstance.TemplateType == 101) && //101 = Document Library
                                             (provisioningOptions.DocumentLibraryFiles))
                                    {
                                        SaveFilesToTemplate(ctx, web, listInstance, template);
                                    }
                                    else if ((listInstance.TemplateType == 109) && //109 = Picture Library
                                             (provisioningOptions.ImageFiles))
                                    {
                                        SaveFilesToTemplate(ctx, web, listInstance, template);
                                    }
                                    break;
                            }
                        }
                    }

                    //if exclude base template from template
                    if (provisioningOptions.ExcludeBaseTemplate)
                    {
                        ProvisioningTemplate baseTemplate = null;
                        WriteMessage($"Info: Loading base template {web.WebTemplate}#{web.Configuration}");

                        baseTemplate = web.GetBaseTemplate(web.WebTemplate, web.Configuration);

                        WriteMessage("Info: Base template loaded");

                        //perform the clean up
                        CleanupTemplate(provisioningOptions, template, baseTemplate);

                        //if not publishing site and publishing feature is activated, then clean publishing features from template
                        if (!baseTemplate.BaseSiteTemplate.Equals(Constants.Enterprise_Wiki_TemplateId, 
                                                                  StringComparison.OrdinalIgnoreCase))
                        {
                            if (web.IsPublishingWeb())
                            {
                                WriteMessage("Info: Publishing feature actived on site");
                                WriteMessage($"Info: Loading {Constants.Enterprise_Wiki_TemplateId} base template");

                                string[] enterWikiArr = Constants.Enterprise_Wiki_TemplateId.Split(new char[] { '#' });

                                short config = Convert.ToInt16(enterWikiArr[1]);

                                baseTemplate = web.GetBaseTemplate(enterWikiArr[0], config);

                                WriteMessage($"Info: Done loading {Constants.Enterprise_Wiki_TemplateId} base template");

                                //perform the clean up
                                CleanupTemplate(provisioningOptions, template, baseTemplate);
                            }
                        }
                    }

                    XMLTemplateProvider provider = new XMLOpenXMLTemplateProvider(ptci.FileConnector as OpenXMLConnector);
                    provider.SaveAs(template, fileNameXML);

                    WriteMessage($"Template saved to {provisioningOptions.TemplatePath}\\{provisioningOptions.TemplateName}.pnp");

                    WriteMessage($"Done creating provisioning template from {web.Title} ( {web.Url} )");

                    result = true;
                }
            }
            catch (Exception ex)
            {
                if (!lbOutput.HorizontalScrollbar)
                {
                    lbOutput.HorizontalScrollbar = true;
                }
                WriteMessage("Error: " + ex.Message.Replace("\r\n", " "));
                if (ex.InnerException != null)
                {
                    WriteMessage("Error: Start of inner exception");
                    WriteMessage("Error: " + ex.InnerException.Message.Replace("\r\n", " "));
                    WriteMessageRange(ex.InnerException.StackTrace.Split(new char[] { '\n', '\r' }, 
                                                                         StringSplitOptions.RemoveEmptyEntries));
                    WriteMessage("Error: End of inner exception");
                }
                WriteMessageRange(ex.StackTrace.Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));
                result = false;
            }
            return result;

        }

        public bool ApplyProvisioningTemplate(ListBox lbOutput, ProvisioningOptions provisioningOptions)
        {
            bool result = false;
            try
            {
                _lbOutput = lbOutput;
                using (var ctx = new ClientContext(provisioningOptions.WebAddress))
                {
                    if (provisioningOptions.AuthenticationRequired)
                    {
                        if (!string.IsNullOrWhiteSpace(provisioningOptions.UserDomain))
                        {
                            ctx.Credentials = new NetworkCredential(provisioningOptions.UserNameOrEmail,
                                                                    provisioningOptions.UserPassword,
                                                                    provisioningOptions.UserDomain);

                        }
                        else
                        {
                            ctx.Credentials = new NetworkCredential(provisioningOptions.UserNameOrEmail,
                                                                    provisioningOptions.UserPassword);
                        }
                    }
                    ctx.RequestTimeout = Timeout.Infinite;

                    string webTitle = string.Empty;

                    WriteMessage($"Connecting to {provisioningOptions.WebAddress}");

                    // Just to output the site details 
                    Web web = ctx.Web;
                    ctx.Load(web, w => w.Title, w => w.Url);
                    ctx.ExecuteQueryRetry();

                    webTitle = web.Title;

                    WriteMessage($"Applying provisioning template to {webTitle} ( {web.Url} )");

                    string fileNamePNP = provisioningOptions.TemplateName + ".pnp";

                    FileConnectorBase fileConnector = new FileSystemConnector(provisioningOptions.TemplatePath, "");

                    XMLTemplateProvider provider = new XMLOpenXMLTemplateProvider(new OpenXMLConnector(fileNamePNP, fileConnector));

                    List<ProvisioningTemplate> templates = provider.GetTemplates();

                    ProvisioningTemplate template = templates[0];

                    WriteMessage($"Base site template in provisioning template is {template.BaseSiteTemplate}");

                    if (template.WebSettings != null)
                    {
                        template.WebSettings.Title = webTitle;
                    }

                    template.Connector = provider.Connector;

                    ProvisioningTemplateApplyingInformation ptai = new ProvisioningTemplateApplyingInformation();

                    ptai.MessagesDelegate = delegate (string message, ProvisioningMessageType messageType)
                    {
                        switch (messageType)
                        {
                            case ProvisioningMessageType.Error:
                                WriteMessage("Error: " + message);
                                break;
                            case ProvisioningMessageType.Progress:
                                WriteMessage("Progress: " + message);
                                break;
                            case ProvisioningMessageType.Warning:
                                WriteMessage("Warning: " + message);
                                break;
                            case ProvisioningMessageType.EasterEgg:
                                WriteMessage("EasterEgg: " + message);
                                break;
                            default:
                                WriteMessage("Unknown: " + message);
                                break;
                        }
                        if (!lbOutput.HorizontalScrollbar)
                        {
                            lbOutput.HorizontalScrollbar = true;
                        }
                    };

                    ptai.ProgressDelegate = delegate (string message, int progress, int total)
                    {
                        WriteMessage(string.Format("{0:00}/{1:00} - {2}", progress, total, message));
                    };

                    web.ApplyProvisioningTemplate(template, ptai);

                    WriteMessage($"Done applying provisioning template to {web.Title} ( {web.Url} )");

                    result = true;
                }
            }
            catch (Exception ex)
            {
                if (!lbOutput.HorizontalScrollbar)
                {
                    lbOutput.HorizontalScrollbar = true;
                }
                WriteMessage("Error: " + ex.Message.Replace("\r\n", " "));
                if (ex.InnerException != null)
                {
                    WriteMessage("Error: Start of inner exception");
                    WriteMessage("Error: " + ex.InnerException.Message.Replace("\r\n", " "));
                    WriteMessageRange(ex.InnerException.StackTrace.Split(new char[] { '\r', '\n' }, 
                                                                         StringSplitOptions.RemoveEmptyEntries));
                    WriteMessage("Error: End of inner exception");
                }
                WriteMessageRange(ex.StackTrace.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
                result = false;
            }
            return result;
        }

        public TemplateItems OpenTemplateForEdit(string templatePath, string templateName, TreeView treeView)
        {
            TemplateItems templateItems = new TemplateItems();

            string fileNamePNP = templateName + ".pnp";

            FileConnectorBase fileConnector = new FileSystemConnector(templatePath, "");

            XMLTemplateProvider provider = new XMLOpenXMLTemplateProvider(new OpenXMLConnector(fileNamePNP, fileConnector));

            List<ProvisioningTemplate> templates = provider.GetTemplates();
            ProvisioningTemplate template = templates[0];

            template.Connector = provider.Connector; //needed when we save back to the template

            EditingTemplate = template;

            treeView.Nodes.Clear();

            KeyValueList templateList = new KeyValueList();

            TreeNode rootNode = new TreeNode($"Template - ( {templateName} )");
            rootNode.Name = "TemplateNode";
            rootNode.Tag = templateItems.AddItem(rootNode.Name, TemplateControlType.ListBox,
                                                 TemplateItemType.Template, null, string.Empty);

            if (template.RegionalSettings != null)
            {
                TreeNode rsNode = new TreeNode("Regional Settings");
                rsNode.Name = "RegionalSettings";
                rsNode.Tag = templateItems.AddItem(rsNode.Name, TemplateControlType.Form,
                                                   TemplateItemType.RegionalSetting,
                                                   GetRegionalSettings(), (string)rootNode.Tag);

                rootNode.Nodes.Add(rsNode);
                templateList.AddKeyValue(rsNode.Text, rsNode.Name);

            }

            if (template.AddIns?.Count > 0)
            {
                TreeNode aiNodes = new TreeNode("Add-Ins");
                aiNodes.Name = "AddIns";
                aiNodes.Tag = templateItems.AddItem(aiNodes.Name, TemplateControlType.ListBox, 
                                                    TemplateItemType.AddInList, null, (string)rootNode.Tag);

                KeyValueList addInsList = new KeyValueList();

                foreach (var addIn in template.AddIns)
                {
                    TreeNode aiNode = new TreeNode(addIn.PackagePath);
                    aiNode.Name = addIn.PackagePath;
                    aiNode.Tag = templateItems.AddItem(aiNode.Name, TemplateControlType.TextBox, 
                                                       TemplateItemType.AddInItem, addIn.Source, 
                                                       (string)aiNodes.Tag);

                    aiNodes.Nodes.Add(aiNode);
                    addInsList.AddKeyValue(addIn.PackagePath, addIn.Source);
                }

                templateItems.SetContent((string)aiNodes.Tag, addInsList);

                rootNode.Nodes.Add(aiNodes);
                templateList.AddKeyValue(aiNodes.Text, aiNodes.Name);

            }

            if (template.ComposedLook?.Name != null)
            {
                TreeNode clNode = new TreeNode("Composed Look");
                clNode.Name = "ComposedLook";
                clNode.Tag = templateItems.AddItem(clNode.Name, TemplateControlType.Form, 
                                                   TemplateItemType.ComposedLook, GetComposedLook(), 
                                                   (string)rootNode.Tag);

                rootNode.Nodes.Add(clNode);
                templateList.AddKeyValue(clNode.Text, clNode.Name);

            }

            if (template.CustomActions?.SiteCustomActions?.Count > 0)
            {
                TreeNode scaNodes = new TreeNode("Site Custom Actions");
                scaNodes.Name = "SiteCustomActions";
                scaNodes.Tag = templateItems.AddItem(scaNodes.Name, TemplateControlType.ListBox,
                                                     TemplateItemType.SiteCustomActionList, null, 
                                                     (string)rootNode.Tag);

                KeyValueList siteCustomActionsList = new KeyValueList();

                foreach (var siteCustomAction in template.CustomActions.SiteCustomActions)
                {
                    TreeNode scaNode = new TreeNode(siteCustomAction.Name);
                    scaNode.Name = siteCustomAction.RegistrationId;
                    scaNode.Tag = templateItems.AddItem(scaNode.Name, TemplateControlType.TextBox,
                                                        TemplateItemType.SiteCustomActionItem,
                                                        GetCustomAction(siteCustomAction),
                                                        (string)scaNodes.Tag);

                    scaNodes.Nodes.Add(scaNode);

                    siteCustomActionsList.AddKeyValue(siteCustomAction.Name, siteCustomAction.RegistrationId);
                }

                templateItems.SetContent((string)scaNodes.Tag, siteCustomActionsList);

                rootNode.Nodes.Add(scaNodes);
                templateList.AddKeyValue(scaNodes.Text, scaNodes.Name);

            }

            if (template.CustomActions?.WebCustomActions?.Count > 0)
            {
                TreeNode wcaNodes = new TreeNode("Web Custom Actions");
                wcaNodes.Name = "WebCustomActions";
                wcaNodes.Tag = templateItems.AddItem(wcaNodes.Name, TemplateControlType.ListBox, 
                                                     TemplateItemType.WebCustomActionList, null,
                                                     (string)rootNode.Tag);

                KeyValueList webCustomActionsList = new KeyValueList();

                foreach (var webCustomAction in template.CustomActions.WebCustomActions)
                {
                    TreeNode wcaNode = new TreeNode(webCustomAction.Name);
                    wcaNode.Name = webCustomAction.RegistrationId;
                    wcaNode.Tag = templateItems.AddItem(wcaNode.Name, TemplateControlType.TextBox,
                                                        TemplateItemType.WebCustomActionItem,
                                                        GetCustomAction(webCustomAction),
                                                        (string)wcaNodes.Tag);

                    wcaNodes.Nodes.Add(wcaNode);

                    webCustomActionsList.AddKeyValue(webCustomAction.Name, webCustomAction.RegistrationId);

                }

                templateItems.SetContent((string)wcaNodes.Tag, webCustomActionsList);

                rootNode.Nodes.Add(wcaNodes);
                templateList.AddKeyValue(wcaNodes.Text, wcaNodes.Name);

            }

            if (template.Features?.SiteFeatures?.Count > 0)
            {
                TreeNode sfNodes = new TreeNode("Site Features");
                sfNodes.Name = "SiteFeatures";

                KeyValueList siteFeaturesList = new KeyValueList();

                foreach (var siteFeature in template.Features.SiteFeatures)
                {
                    siteFeaturesList.AddKeyValue(siteFeature.Id.ToString("B"), siteFeature.Id.ToString("B")); //B = {} format
                }

                sfNodes.Tag = templateItems.AddItem(sfNodes.Name, TemplateControlType.ListBox, 
                                                    TemplateItemType.SiteFeatureList, 
                                                    siteFeaturesList, 
                                                    (string)rootNode.Tag);

                rootNode.Nodes.Add(sfNodes);
                templateList.AddKeyValue(sfNodes.Text, sfNodes.Name);

            }

            if (template.Features?.WebFeatures?.Count > 0)
            {
                TreeNode wfNodes = new TreeNode("Web Features");
                wfNodes.Name = "WebFeatures";

                KeyValueList webFeaturesList = new KeyValueList();

                foreach (var webFeature in template.Features.WebFeatures)
                {
                    webFeaturesList.AddKeyValue(webFeature.Id.ToString("B"), webFeature.Id.ToString("B"));
                }

                wfNodes.Tag = templateItems.AddItem(wfNodes.Name, TemplateControlType.ListBox, 
                                                    TemplateItemType.WebFeatureList, 
                                                    webFeaturesList, 
                                                    (string)rootNode.Tag);

                rootNode.Nodes.Add(wfNodes);
                templateList.AddKeyValue(wfNodes.Text, wfNodes.Name);

            }

            if (template.ContentTypes?.Count > 0)
            {
                TreeNode ctNodes = new TreeNode("Content Types");
                ctNodes.Name = "ContentTypes";
                ctNodes.Tag = templateItems.AddItem(ctNodes.Name, TemplateControlType.ListBox, 
                                                    TemplateItemType.ContentTypeList, null, 
                                                    (string)rootNode.Tag);

                KeyValueList contentTypeList = new KeyValueList();
                
                foreach (var contentType in template.ContentTypes)
                {
                    KeyValueList contentTypeGroup = null;
                    TreeNode ctgNode = null;
                    TreeNode[] ctgNodes = ctNodes.Nodes.Find(contentType.Group, false);
                    if (ctgNodes?.Length > 0)
                    {
                        ctgNode = ctgNodes[0];
                        contentTypeGroup = templateItems.GetContent((string)ctgNode.Tag) as KeyValueList;

                    }
                    else
                    {
                        ctgNode = new TreeNode(contentType.Group);
                        ctgNode.Name = contentType.Group;
                        ctgNode.Tag = templateItems.AddItem(ctgNode.Name, TemplateControlType.ListBox,
                                                            TemplateItemType.ContentTypeGroup, null,
                                                            (string)ctNodes.Tag);

                        contentTypeGroup = new KeyValueList();

                        ctNodes.Nodes.Add(ctgNode);

                    }

                    TreeNode ctNode = new TreeNode(contentType.Name);
                    ctNode.Name = contentType.Id;
                    ctNode.Tag = templateItems.AddItem(contentType.Name, TemplateControlType.TextBox,
                                                       TemplateItemType.ContentTypeItem,
                                                       GetContentType(contentType.Id),
                                                       (string)ctgNode.Tag);

                    ctgNode.Nodes.Add(ctNode);

                    contentTypeGroup.AddKeyValue(contentType.Name, contentType.Id);

                    templateItems.SetContent((string)ctgNode.Tag, contentTypeGroup);

                    if (!contentTypeList.Exists(p => p.Key.Equals(contentType.Group, 
                                                StringComparison.OrdinalIgnoreCase)))
                    {
                        contentTypeList.AddKeyValue(contentType.Group, contentType.Group);

                    }

                }

                templateItems.SetContent((string)ctNodes.Tag, contentTypeList);

                rootNode.Nodes.Add(ctNodes);

                templateList.AddKeyValue(ctNodes.Text, ctNodes.Name);

            }

            if (template.SiteFields?.Count > 0)
            {
                TreeNode sfNodes = new TreeNode("Site Fields");
                sfNodes.Name = "SiteFields";
                sfNodes.Tag = templateItems.AddItem(sfNodes.Name, TemplateControlType.ListBox, 
                                                    TemplateItemType.SiteFieldList, null, 
                                                    (string)rootNode.Tag);

                KeyValueList siteFieldsList = new KeyValueList();

                foreach (var siteField in template.SiteFields)
                {
                    XElement fieldElement = XElement.Parse(siteField.SchemaXml);
                    string fieldGroup = "Undefined Group";
                    if (fieldElement.Attribute("Group") != null)
                    {
                        fieldGroup = fieldElement.Attribute("Group").Value;
                    }
                    string fieldID = fieldElement.Attribute("ID").Value;
                    string fieldName = fieldElement.Attribute("Name").Value;

                    KeyValueList siteFieldsGroup = null;
                    TreeNode sfgNode = null;
                    TreeNode[] sfgNodes = sfNodes.Nodes.Find(fieldGroup, false);
                    if (sfgNodes?.Length > 0)
                    {
                        sfgNode = sfgNodes[0];
                        siteFieldsGroup = templateItems.GetContent((string)sfgNode.Tag) as KeyValueList;

                    }
                    else
                    {
                        sfgNode = new TreeNode(fieldGroup);
                        sfgNode.Name = fieldGroup;
                        sfgNode.Tag = templateItems.AddItem(fieldGroup, TemplateControlType.ListBox,
                                                            TemplateItemType.SiteFieldGroup, null,
                                                            (string)sfNodes.Tag);

                        siteFieldsGroup = new KeyValueList();

                        sfNodes.Nodes.Add(sfgNode);

                    }

                    TreeNode sfNode = new TreeNode(fieldName);

                    string fieldXml = fieldElement.ToString(SaveOptions.None);
                    int gtFirst = fieldXml.IndexOf('>', 0);
                    string fieldText = fieldXml.Substring(0, gtFirst).Replace("\" ", "\"\r\n       ") +
                                       fieldXml.Substring(gtFirst);

                    sfNode.Name = fieldID;
                    sfNode.Tag = templateItems.AddItem(fieldID, TemplateControlType.TextBox,
                                                       TemplateItemType.SiteFieldItem, fieldText,
                                                       (string)sfgNode.Tag);

                    sfgNode.Nodes.Add(sfNode);

                    siteFieldsGroup.AddKeyValue(fieldName, fieldID);

                    templateItems.SetContent((string)sfgNode.Tag, siteFieldsGroup);

                    if (!siteFieldsList.Exists(p => p.Key.Equals(fieldGroup, 
                                               StringComparison.OrdinalIgnoreCase)))
                    {
                        siteFieldsList.AddKeyValue(fieldGroup, fieldGroup);

                    }

                }

                templateItems.SetContent((string)sfNodes.Tag, siteFieldsList);

                rootNode.Nodes.Add(sfNodes);
                templateList.AddKeyValue(sfNodes.Text, sfNodes.Name);

            }

            if (template.Files?.Count > 0)
            {
                TreeNode fNodes = new TreeNode("Files");
                fNodes.Name = "Files";
                fNodes.Tag = templateItems.AddItem(fNodes.Name, TemplateControlType.ListBox,
                                                   TemplateItemType.FileList, null, 
                                                   (string)rootNode.Tag);

                KeyValueList filesList = new KeyValueList();

                foreach (var file in template.Files)
                {
                    TreeNode fNode = new TreeNode(file.Src);
                    fNode.Name = file.Src;
                    fNode.Tag = templateItems.AddItem(fNode.Name, TemplateControlType.TextBox,
                                                      TemplateItemType.FileItem,
                                                      GetPNPFile(file),
                                                      (string)fNodes.Tag);

                    if (file.WebParts?.Count > 0)
                    {
                        TreeNode fwpNodes = new TreeNode("WebParts");
                        fwpNodes.Name = fNode.Name + "_WebParts";
                        fwpNodes.Tag = templateItems.AddItem(fwpNodes.Name, TemplateControlType.ListBox,
                                                             TemplateItemType.FileWebPartsList, null,
                                                             (string)fNode.Tag);

                        KeyValueList webPartList = new KeyValueList();

                        foreach (var webPart in file.WebParts)
                        {
                            WebPart newWP = new WebPart()
                            {
                                Column = webPart.Column,
                                Order = webPart.Order,
                                Row = webPart.Row,
                                Title = webPart.Title,
                                Zone = webPart.Zone

                            };

                            TreeNode fwpNode = new TreeNode(newWP.Title);
                            fwpNode.Name = fwpNodes.Name + "_" + newWP.Title;
                            fwpNode.Tag = templateItems.AddItem(fwpNode.Name, TemplateControlType.TextBox,
                                                                TemplateItemType.FileWebPartItem,
                                                                JsonConvert.SerializeObject(newWP, Newtonsoft.Json.Formatting.Indented),
                                                                (string)fwpNodes.Tag);

                            webPartList.AddKeyValue(newWP.Title, fwpNode.Name);

                            TreeNode fwpcNode = new TreeNode("Contents");
                            fwpcNode.Name = fwpNode.Name + "_Contents";
                            XElement fwpcElement = XElement.Parse(webPart.Contents);

                            string fieldXml = fwpcElement.ToString(SaveOptions.None);

                            fwpcNode.Tag = templateItems.AddItem(fwpcNode.Name, TemplateControlType.TextBox,
                                                                 TemplateItemType.FileWebPartItemContent,
                                                                 fieldXml,
                                                                 (string)fwpNode.Tag);

                            fwpNode.Nodes.Add(fwpcNode);

                            fwpNodes.Nodes.Add(fwpNode);

                        }

                        templateItems.SetContent((string)fwpNodes.Tag, webPartList);

                        fNode.Nodes.Add(fwpNodes);

                    }

                    fNodes.Nodes.Add(fNode);

                    filesList.AddKeyValue(file.Src, file.Src);

                }

                templateItems.SetContent((string)fNodes.Tag, filesList);

                rootNode.Nodes.Add(fNodes);
                templateList.AddKeyValue(fNodes.Text, fNodes.Name);

            }

            if (template.Lists?.Count > 0)
            {
                TreeNode lNodes = new TreeNode("Lists");
                lNodes.Name = "Lists";
                lNodes.Tag = templateItems.AddItem(lNodes.Name, TemplateControlType.ListBox, 
                                                   TemplateItemType.ListList, null, 
                                                   (string)rootNode.Tag);

                KeyValueList listsList = new KeyValueList();

                foreach (var list in template.Lists)
                {
                    TreeNode lNode = new TreeNode(list.Title);
                    lNode.Name = list.Url;
                    lNode.Tag = templateItems.AddItem(lNode.Name, TemplateControlType.TextBox,
                                                      TemplateItemType.ListItem,
                                                      GetListInstance(list.Url),
                                                      (string)lNodes.Tag);

                    if (list.Fields?.Count > 0)
                    {
                        TreeNode fNodes = new TreeNode("Fields");
                        fNodes.Name = lNode.Name + "_ListFields";
                        fNodes.Tag = templateItems.AddItem(fNodes.Name, TemplateControlType.ListBox, 
                                                           TemplateItemType.ListFieldList, null,
                                                           (string)lNode.Tag);

                        KeyValueList fieldsList = new KeyValueList();

                        foreach (var field in list.Fields)
                        {
                            XElement fieldElement = XElement.Parse(field.SchemaXml);
                            string fieldID = fieldElement.Attribute("ID").Value;
                            string fieldName = fieldElement.Attribute("Name").Value;

                            TreeNode fNode = new TreeNode(fieldName);

                            string fieldXml = fieldElement.ToString(SaveOptions.None);
                            //Arrange first element attributes in rows
                            int gtFirst = fieldXml.IndexOf('>', 0);
                            string fieldText = fieldXml.Substring(0, gtFirst).Replace("\" ", "\"\r\n       ") + 
                                               fieldXml.Substring(gtFirst);

                            fNode.Name = fieldID;
                            fNode.Tag = templateItems.AddItem(fieldID, TemplateControlType.TextBox,
                                                              TemplateItemType.ListFieldItem, fieldText,
                                                              (string)fNodes.Tag);

                            fNodes.Nodes.Add(fNode);
                            fieldsList.AddKeyValue(fieldName, fieldID);

                        }

                        templateItems.SetContent((string)fNodes.Tag, fieldsList);

                        lNode.Nodes.Add(fNodes);

                    }

                    if (list.Views?.Count > 0)
                    {
                        TreeNode vNodes = new TreeNode("Views");
                        vNodes.Name = lNode.Name + "_ListViews";
                        vNodes.Tag = templateItems.AddItem(vNodes.Name, TemplateControlType.ListBox,
                                                           TemplateItemType.ListViewList, null,
                                                           (string)lNode.Tag);

                        KeyValueList viewsList = new KeyValueList();

                        foreach (var view in list.Views)
                        {
                            XElement viewElement = XElement.Parse(view.SchemaXml);
                            string viewName = viewElement.Attribute("Name").Value;
                            string displayName = viewElement.Attribute("DisplayName").Value;

                            TreeNode vNode = new TreeNode(displayName);

                            string viewXml = viewElement.ToString(SaveOptions.None);
                            //Arrange first element attributes in rows
                            int gtFirst = viewXml.IndexOf('>', 0);
                            string viewText = viewXml.Substring(0, gtFirst).Replace("\" ", "\"\r\n      ") + 
                                              viewXml.Substring(gtFirst);

                            vNode.Name = viewName;
                            vNode.Tag = templateItems.AddItem(vNode.Name, TemplateControlType.TextBox,
                                                              TemplateItemType.ListViewItem, viewText,
                                                              (string)vNodes.Tag);

                            vNodes.Nodes.Add(vNode);

                            viewsList.AddKeyValue(displayName, viewName);

                        }

                        templateItems.SetContent((string)vNodes.Tag, viewsList);

                        lNode.Nodes.Add(vNodes);

                    }

                    listsList.AddKeyValue(list.Title, list.Url);

                    lNodes.Nodes.Add(lNode);

                }

                templateItems.SetContent((string)lNodes.Tag, listsList);

                rootNode.Nodes.Add(lNodes);
                templateList.AddKeyValue(lNodes.Text, lNodes.Name);

            }

            if (template.Localizations?.Count > 0)
            {
                TreeNode glNodes = new TreeNode("Localizations");
                glNodes.Name = "Localizations";
                glNodes.Tag = templateItems.AddItem(glNodes.Name, TemplateControlType.ListBox, 
                                                    TemplateItemType.LocalizationsList, null, 
                                                    (string)rootNode.Tag);

                KeyValueList localizationsList = new KeyValueList();

                foreach (var localization in template.Localizations)
                {
                    TreeNode glNode = new TreeNode(localization.Name);
                    glNode.Name = localization.LCID.ToString();
                    glNode.Tag = templateItems.AddItem(glNode.Name, TemplateControlType.TextBox,
                                                       TemplateItemType.LocalizationsItem,
                                                       GetLocalization(localization),
                                                       (string)glNodes.Tag);

                    glNodes.Nodes.Add(glNode);

                    localizationsList.AddKeyValue(localization.Name, localization.LCID.ToString());

                }

                templateItems.SetContent((string)glNodes.Tag, localizationsList);

                rootNode.Nodes.Add(glNodes);
                templateList.AddKeyValue(glNodes.Text, glNodes.Name);

            }

            //Navigation to do

            if (template.Pages?.Count > 0)
            {
                TreeNode pNodes = new TreeNode("Pages");
                pNodes.Name = "Pages";
                pNodes.Tag = templateItems.AddItem(pNodes.Name, TemplateControlType.ListBox, 
                                                   TemplateItemType.PageList, null, 
                                                   (string)rootNode.Tag);

                KeyValueList pageList = new KeyValueList();

                foreach (var page in template.Pages)
                {
                    TreeNode pNode = new TreeNode(page.Url);
                    pNode.Name = page.Url;
                    pNode.Tag = templateItems.AddItem(pNode.Name, TemplateControlType.TextBox,
                                                      TemplateItemType.PageItem,
                                                      GetPageContent(page),
                                                      (string)pNodes.Tag);

                    pNodes.Nodes.Add(pNode);
                    pageList.AddKeyValue(page.Url, page.Url);

                }

                templateItems.SetContent((string)pNodes.Tag, pageList);

                rootNode.Nodes.Add(pNodes);

                templateList.AddKeyValue(pNodes.Text, pNodes.Name);

            }

            if (template.Properties?.Count > 0)
            {
                TreeNode pNode = new TreeNode("Properties");
                pNode.Name = "Properties";
                pNode.Tag = templateItems.AddItem(pNode.Name, TemplateControlType.ListView, 
                                                  TemplateItemType.PropertiesList, null, 
                                                  (string)rootNode.Tag);

                KeyValueList propertiesList = new KeyValueList();

                foreach (var property in template.Properties)
                {
                    propertiesList.AddKeyValue(property.Key, property.Value);

                }

                templateItems.SetContent((string)pNode.Tag, propertiesList);

                rootNode.Nodes.Add(pNode);

                templateList.AddKeyValue(pNode.Text, pNode.Name);

            }

            if (template.PropertyBagEntries?.Count > 0)
            {
                TreeNode pbeNodes = new TreeNode("Property Bag Entries");
                pbeNodes.Name = "PropertyBagEntries";
                pbeNodes.Tag = templateItems.AddItem(pbeNodes.Name, TemplateControlType.ListView, 
                                                     TemplateItemType.PropertyBagEntriesList, null, 
                                                     (string)rootNode.Tag);

                KeyValueList propertyBagEntriesList = new KeyValueList();

                foreach (var propertyBagEntry in template.PropertyBagEntries)
                {
                    propertyBagEntriesList.AddKeyValue(propertyBagEntry.Key, propertyBagEntry.Value);

                }

                templateItems.SetContent((string)pbeNodes.Tag, propertyBagEntriesList);

                rootNode.Nodes.Add(pbeNodes);

                templateList.AddKeyValue(pbeNodes.Text, pbeNodes.Name);

            }

            if (template.Publishing != null)
            {
                TreeNode pNode = new TreeNode("Publishing");
                pNode.Name = "Publishing";

                pNode.Tag = templateItems.AddItem(pNode.Name, TemplateControlType.TextBox,
                                                  TemplateItemType.PublishingList,
                                                  GetPublishing(template.Publishing), 
                                                  (string)rootNode.Tag);

                rootNode.Nodes.Add(pNode);

                templateList.AddKeyValue(pNode.Text, pNode.Name);

            }

            if (template.SupportedUILanguages?.Count > 0)
            {
                TreeNode suilNode = new TreeNode("Supported UI Languages");
                suilNode.Name = "SupportedUILanguages";
                suilNode.Tag = templateItems.AddItem(suilNode.Name, TemplateControlType.ListBox, 
                                                     TemplateItemType.SupportedUILanguagesList, null, 
                                                     (string)rootNode.Tag);

                KeyValueList supportedUILanguages = new KeyValueList();

                foreach(var suil in template.SupportedUILanguages)
                {
                    supportedUILanguages.AddKeyValue(suil.LCID.ToString(), suil.LCID.ToString());

                }

                templateItems.SetContent((string)suilNode.Tag, supportedUILanguages);

                rootNode.Nodes.Add(suilNode);

                templateList.AddKeyValue(suilNode.Text, suilNode.Name);

            }

            if (template.TermGroups?.Count > 0)
            {
                TreeNode tgNodes = new TreeNode("Term Groups");
                tgNodes.Name = "TermGroups";
                tgNodes.Tag = templateItems.AddItem(tgNodes.Name, TemplateControlType.ListBox, 
                                                    TemplateItemType.TermGroupList, null, 
                                                    (string)rootNode.Tag);

                KeyValueList termGroupsList = new KeyValueList();

                foreach (var termGroup in template.TermGroups)
                {
                    TreeNode tgNode = new TreeNode(termGroup.Name);
                    tgNode.Name = termGroup.Id.ToString("N");
                    tgNode.Tag = templateItems.AddItem(tgNode.Name, TemplateControlType.TextBox,
                                                       TemplateItemType.TermGroupItem,
                                                       GetTermGroup(termGroup.Id),
                                                       (string)tgNodes.Tag);
                    

                    if (termGroup.TermSets?.Count > 0)
                    {
                        TreeNode tsNodes = new TreeNode("Term Sets");
                        tsNodes.Name = tgNode.Name + "_TermSets";
                        tsNodes.Tag = templateItems.AddItem(tsNodes.Name, TemplateControlType.ListBox,
                                                            TemplateItemType.TermSetList, null,
                                                            (string)tgNode.Tag);

                        KeyValueList termSetsList = new KeyValueList();

                        foreach (var termSet in termGroup.TermSets)
                        {
                            TreeNode tsNode = new TreeNode(termSet.Name);
                            tsNode.Name = termSet.Id.ToString("N");
                            tsNode.Tag = templateItems.AddItem(tsNode.Name, TemplateControlType.TextBox,
                                                               TemplateItemType.TermSetItem,
                                                               GetTermSet(termSet),
                                                               (string)tsNodes.Tag);
                            
                            tsNodes.Nodes.Add(tsNode);

                            termSetsList.AddKeyValue(termSet.Name, tsNodes.Name);

                        }

                        templateItems.SetContent((string)tsNodes.Tag, termSetsList);

                        tgNode.Nodes.Add(tsNodes);

                    }

                    tgNodes.Nodes.Add(tgNode);

                    termGroupsList.AddKeyValue(termGroup.Name, tgNode.Name);

                }

                templateItems.SetContent((string)tgNodes.Tag, termGroupsList);

                rootNode.Nodes.Add(tgNodes);
                templateList.AddKeyValue(tgNodes.Text, tgNodes.Name);

            }

            if (template.WebSettings != null)
            {
                TreeNode wsNode = new TreeNode("Web Settings");
                wsNode.Name = "WebSettings";
                wsNode.Tag = templateItems.AddItem(wsNode.Name, TemplateControlType.Form, 
                                                   TemplateItemType.WebSetting, 
                                                   GetWebSettings(template.WebSettings), 
                                                   (string)rootNode.Tag);

                rootNode.Nodes.Add(wsNode);

                templateList.AddKeyValue(wsNode.Text, wsNode.Name);

            }

            if (template.Workflows?.WorkflowDefinitions?.Count > 0)
            {
                TreeNode wwdNodes = new TreeNode("Workflow Definitions");
                wwdNodes.Name = "WorkflowDefinitions";
                wwdNodes.Tag = templateItems.AddItem(wwdNodes.Name, TemplateControlType.ListBox, 
                                                     TemplateItemType.WorkflowDefinitionList, null, 
                                                     (string)rootNode.Tag);

                KeyValueList workflowDefinitionsList = new KeyValueList();

                foreach (var workflowDefinition in template.Workflows.WorkflowDefinitions)
                {
                    TreeNode wwdNode = new TreeNode(workflowDefinition.DisplayName);
                    wwdNode.Name = workflowDefinition.Id.ToString("N");
                    wwdNode.Tag = templateItems.AddItem(wwdNode.Name, TemplateControlType.TextBox,
                                                        TemplateItemType.WorkflowDefinitionItem,
                                                        GetWorkflowDefinition(workflowDefinition.Id),
                                                        (string)wwdNodes.Tag);

                    wwdNodes.Nodes.Add(wwdNode);

                    workflowDefinitionsList.AddKeyValue(workflowDefinition.DisplayName, wwdNode.Name);

                }

                templateItems.SetContent((string)wwdNodes.Tag, workflowDefinitionsList);

                rootNode.Nodes.Add(wwdNodes);

                templateList.AddKeyValue(wwdNodes.Text, wwdNodes.Name);

            }

            if (template.Workflows?.WorkflowSubscriptions?.Count > 0)
            {
                TreeNode wwsNodes = new TreeNode("Workflow Subscriptions");
                wwsNodes.Name = "WorkflowSubscriptions";
                wwsNodes.Tag = templateItems.AddItem(wwsNodes.Name, TemplateControlType.ListBox, 
                                                     TemplateItemType.WorkflowSubscriptionList, null, 
                                                     (string)rootNode.Tag);

                KeyValueList workflowSubscriptionsList = new KeyValueList();

                foreach (var workflowSubscription in template.Workflows.WorkflowSubscriptions)
                {
                    TreeNode wwsNode = new TreeNode(workflowSubscription.Name);
                    wwsNode.Name = workflowSubscription.Name;
                    wwsNode.Tag = templateItems.AddItem(wwsNode.Name, TemplateControlType.TextBox,
                                                        TemplateItemType.WorkflowSubscriptionItem,
                                                        GetWorkflowSubscription(workflowSubscription.Name),
                                                        (string)wwsNodes.Tag);

                    wwsNodes.Nodes.Add(wwsNode);

                    workflowSubscriptionsList.AddKeyValue(workflowSubscription.Name, workflowSubscription.Name);

                }

                templateItems.SetContent((string)wwsNodes.Tag, workflowSubscriptionsList);

                rootNode.Nodes.Add(wwsNodes);
                templateList.AddKeyValue(wwsNodes.Text, wwsNodes.Name);

            }

            templateItems.SetContent((string)rootNode.Tag, templateList);

            rootNode.Expand();

            treeView.Nodes.Add(rootNode);

            return templateItems;

        } //OpenTemplateForEdit

        private int[] GetRegionalSettings()
        {
            int[] result = null;
            if (EditingTemplate?.RegionalSettings != null)
            {
                result = new int[]
                {
                    EditingTemplate.RegionalSettings.AdjustHijriDays,
                    (int)EditingTemplate.RegionalSettings.AlternateCalendarType,
                    (int)EditingTemplate.RegionalSettings.CalendarType,
                    EditingTemplate.RegionalSettings.Collation,
                    (int)EditingTemplate.RegionalSettings.FirstDayOfWeek,
                    EditingTemplate.RegionalSettings.FirstWeekOfYear,
                    EditingTemplate.RegionalSettings.LocaleId,
                    (EditingTemplate.RegionalSettings.ShowWeeks ? 1 : 0),
                    (EditingTemplate.RegionalSettings.Time24 ? 1 : 0),
                    EditingTemplate.RegionalSettings.TimeZone,
                    (int)EditingTemplate.RegionalSettings.WorkDayEndHour / 60,
                    EditingTemplate.RegionalSettings.WorkDays,
                    (int)EditingTemplate.RegionalSettings.WorkDayStartHour / 60
                };
                //Note: Ensure that the above array is populated in the order of the RegionalSettingProperties enum
            }

            return result;

        } //GetRegionalSettingProperty

        private string[] GetComposedLook()
        {
            string[] result = null;
            if (EditingTemplate?.ComposedLook != null)
            {
                result = new string[]
                {
                    EditingTemplate.ComposedLook.Name,
                    EditingTemplate.ComposedLook.BackgroundFile,
                    EditingTemplate.ComposedLook.ColorFile,
                    EditingTemplate.ComposedLook.FontFile,
                    EditingTemplate.ComposedLook.Version.ToString()
                };
                //Note: Ensure that the above array is populated in the order of the ComposedLookProperties enum
            }

            return result;

        } //GetComposedLook

        private string GetContentType(string contentTypeId)
        {
            string result = string.Empty;
            if (EditingTemplate?.ContentTypes != null)
            {
                PnPModel.ContentType contentType = EditingTemplate.ContentTypes.Find(p => p.Id.Equals(contentTypeId, 
                                                                                     StringComparison.OrdinalIgnoreCase));
                if (contentType != null)
                {
                    PnPModel.ContentType newCT = new PnPModel.ContentType()
                    {
                        Id = contentType.Id,
                        Description = contentType.Description,
                        DisplayFormUrl = contentType.DisplayFormUrl,
                        DocumentSetTemplate = contentType.DocumentSetTemplate,
                        DocumentTemplate = contentType.DocumentTemplate,
                        EditFormUrl = contentType.EditFormUrl,
                        Group = contentType.Group,
                        Hidden = contentType.Hidden,
                        Name = contentType.Name,
                        NewFormUrl = contentType.NewFormUrl,
                        Overwrite = contentType.Overwrite,
                        ReadOnly = contentType.ReadOnly,
                        Sealed = contentType.Sealed

                    };

                    if (contentType.FieldRefs?.Count > 0)
                    {
                        newCT.FieldRefs.AddRange(contentType.FieldRefs);

                    }

                    result = JsonConvert.SerializeObject(newCT, Newtonsoft.Json.Formatting.Indented);

                }

            }

            return result;

        } //GetContentType

        private string GetListInstance(string url)
        {
            string result = string.Empty;
            if (EditingTemplate?.Lists != null)
            {
                ListInstance listInstance = EditingTemplate.Lists.Find(p => p.Url.Equals(url,
                                                                                         StringComparison.OrdinalIgnoreCase));
                if (listInstance != null)
                {
                    ListInstance newLI = new ListInstance()
                    {
                        ContentTypesEnabled = listInstance.ContentTypesEnabled,
                        Description = listInstance.Description,
                        DocumentTemplate = listInstance.DocumentTemplate,
                        DraftVersionVisibility = listInstance.DraftVersionVisibility,
                        EnableAttachments = listInstance.EnableAttachments,
                        EnableFolderCreation = listInstance.EnableFolderCreation,
                        EnableMinorVersions = listInstance.EnableMinorVersions,
                        EnableModeration = listInstance.EnableModeration,
                        EnableVersioning = listInstance.EnableVersioning,
                        ForceCheckout = listInstance.ForceCheckout,
                        Hidden = listInstance.Hidden,
                        MaxVersionLimit = listInstance.MaxVersionLimit,
                        MinorVersionLimit = listInstance.MinorVersionLimit,
                        OnQuickLaunch = listInstance.OnQuickLaunch,
                        RemoveExistingContentTypes = listInstance.RemoveExistingContentTypes,
                        RemoveExistingViews = listInstance.RemoveExistingViews,
                        TemplateFeatureID = listInstance.TemplateFeatureID,
                        TemplateType = listInstance.TemplateType,
                        Title = listInstance.Title,
                        Url = listInstance.Url

                    };

                    if (listInstance.ContentTypeBindings?.Count > 0)
                    {
                        newLI.ContentTypeBindings.AddRange(listInstance.ContentTypeBindings);

                    }

                    if (listInstance.DataRows?.Count > 0)
                    {
                        newLI.DataRows.AddRange(listInstance.DataRows);

                    }

                    if (listInstance.FieldDefaults?.Count > 0)
                    {
                        foreach (var kvp in listInstance.FieldDefaults)
                        {
                            newLI.FieldDefaults.Add(kvp.Key, kvp.Value);

                        }

                    }

                    if (listInstance.FieldRefs?.Count > 0)
                    {
                        newLI.FieldRefs.AddRange(listInstance.FieldRefs);

                    }

                    if (listInstance.Folders?.Count > 0)
                    {
                        newLI.Folders.AddRange(listInstance.Folders);

                    }

                    if (listInstance.Security != null)
                    {
                        newLI.Security = new ObjectSecurity()
                        {
                            ClearSubscopes = listInstance.Security.ClearSubscopes,
                            CopyRoleAssignments = listInstance.Security.CopyRoleAssignments

                        };

                        newLI.Security.RoleAssignments.AddRange(listInstance.Security.RoleAssignments);

                    }

                    if (listInstance.UserCustomActions?.Count > 0)
                    {
                        newLI.UserCustomActions.AddRange(listInstance.UserCustomActions);

                    }

                    //ensure fields are empty as they are handled elsewhere
                    newLI.Fields.Clear();
                    //ensure views are empty as they are handled elsewhere
                    newLI.Views.Clear();

                    result = JsonConvert.SerializeObject(newLI, Newtonsoft.Json.Formatting.Indented);

                }

            }

            return result;

        } //GetListInstance

        private string[] GetWebSettings(WebSettings webSettings)
        {
            string[] result = null;
            if (webSettings != null)
            {
                result = new string[]
               {
                   webSettings.AlternateCSS,
                   webSettings.CustomMasterPageUrl,
                   webSettings.Description,
                   webSettings.MasterPageUrl,
                   (webSettings.NoCrawl ? "1" : "0"),
                   webSettings.RequestAccessEmail,
                   webSettings.SiteLogo,
                   webSettings.Title,
                   webSettings.WelcomePage

               };
                //Note: Ensure that the above are added in the order as defined in the WebSettingProperties enum
            }

            return result;

        } //GetWebSettings

        private string GetWorkflowDefinition(Guid WorkflowDefinitionId)
        {
            string result = string.Empty;
            if (EditingTemplate?.Workflows?.WorkflowDefinitions != null)
            {
                WorkflowDefinition workflowDefinition = EditingTemplate.Workflows.WorkflowDefinitions.Find(p => 
                                                        p.Id.Equals(WorkflowDefinitionId));

                if (workflowDefinition != null)
                {
                    WorkflowDefinition newWD = new WorkflowDefinition()
                    {
                        AssociationUrl = workflowDefinition.AssociationUrl,
                        Description = workflowDefinition.Description,
                        DisplayName = workflowDefinition.DisplayName,
                        DraftVersion = workflowDefinition.DraftVersion,
                        FormField = workflowDefinition.FormField,
                        Id = workflowDefinition.Id,
                        InitiationUrl = workflowDefinition.InitiationUrl,
                        Published = workflowDefinition.Published,
                        RequiresAssociationForm = workflowDefinition.RequiresAssociationForm,
                        RequiresInitiationForm = workflowDefinition.RequiresInitiationForm,
                        RestrictToScope = workflowDefinition.RestrictToScope,
                        RestrictToType = workflowDefinition.RestrictToType,
                        XamlPath = workflowDefinition.XamlPath

                    };

                    if (workflowDefinition.Properties?.Count > 0)
                    {
                        foreach (var property in workflowDefinition.Properties)
                        {
                            newWD.Properties.Add(property.Key, property.Value);
                        }

                    }

                    result = JsonConvert.SerializeObject(newWD, Newtonsoft.Json.Formatting.Indented);

                }

            }

            return result;

        } //GetWorkflowDefinition

        private string GetWorkflowSubscription(string workflowSubscriptionName)
        {
            string result = string.Empty;
            if (EditingTemplate?.Workflows?.WorkflowSubscriptions != null)
            {
                WorkflowSubscription workflowSubscription = EditingTemplate.Workflows.WorkflowSubscriptions
                                                                           .Find(p => p.Name.Equals(workflowSubscriptionName,
                                                                                                    StringComparison.OrdinalIgnoreCase));
                if (workflowSubscription != null)
                {
                    WorkflowSubscription newWS = new WorkflowSubscription()
                    {
                        DefinitionId = workflowSubscription.DefinitionId,
                        Enabled = workflowSubscription.Enabled,
                        EventSourceId = workflowSubscription.EventSourceId,
                        EventTypes = workflowSubscription.EventTypes,
                        ListId = workflowSubscription.ListId,
                        ManualStartBypassesActivationLimit = workflowSubscription.ManualStartBypassesActivationLimit,
                        Name = workflowSubscription.Name,
                        ParentContentTypeId = workflowSubscription.ParentContentTypeId,
                        StatusFieldName = workflowSubscription.StatusFieldName

                    };

                    if (workflowSubscription.PropertyDefinitions?.Count > 0)
                    {
                        foreach (var propertyDefinition in workflowSubscription.PropertyDefinitions)
                        {
                            newWS.PropertyDefinitions.Add(propertyDefinition.Key, propertyDefinition.Value);

                        }

                    }

                    result = JsonConvert.SerializeObject(newWS, Newtonsoft.Json.Formatting.Indented);

                }

            }

            return result;

        } //GetWorkflowSubscription

        private string GetCustomAction(CustomAction customAction)
        {
            string result = string.Empty;
            if (customAction != null)
            {
                CustomAction newCA = new CustomAction()
                {
                    CommandUIExtension = customAction.CommandUIExtension,
                    Description = customAction.Description,
                    Enabled = customAction.Enabled,
                    Group = customAction.Group,
                    ImageUrl = customAction.ImageUrl,
                    Location = customAction.Location,
                    Name = customAction.Name,
                    RegistrationId = customAction.RegistrationId,
                    RegistrationType = customAction.RegistrationType,
                    Remove = customAction.Remove,
                    Rights = customAction.Rights,
                    ScriptBlock = customAction.ScriptBlock,
                    ScriptSrc = customAction.ScriptSrc,
                    Sequence = customAction.Sequence,
                    Title = customAction.Title,
                    Url = customAction.Url

                };

                result = JsonConvert.SerializeObject(newCA, Newtonsoft.Json.Formatting.Indented);

            }

            return result;

        } //GetCustomAction

        private string GetPNPFile(PnPModel.File file)
        {
            string result = string.Empty;
            if (file != null)
            {
                PnPModel.File newF = new PnPModel.File()
                {
                    Folder = file.Folder,
                    Level = file.Level,
                    Overwrite = file.Overwrite,
                    Security = file.Security,
                    Src = file.Src

                };

                if (file.Properties?.Count > 0)
                {
                    foreach (var property in file.Properties)
                    {
                        newF.Properties.Add(property.Key, property.Value);

                    }

                }

                //Do not add Webparts here as they are handled somewhere else

                result = JsonConvert.SerializeObject(newF, Newtonsoft.Json.Formatting.Indented);

            }

            return result;

        } //GetPNPFile

        private string GetLocalization(Localization localization)
        {
            string result = string.Empty;
            if (localization != null)
            {
                Localization newL = new Localization()
                {
                    LCID = localization.LCID,
                    Name = localization.Name,
                    ResourceFile = localization.ResourceFile

                };

                result = JsonConvert.SerializeObject(newL, Newtonsoft.Json.Formatting.Indented);

            }

            return result;

        } //GetLocalization

        private string GetPageContent(Page page)
        {
            string result = string.Empty;
            if (page != null)
            {
                Page newP = new Page()
                {
                    Layout = page.Layout,
                    Overwrite = page.Overwrite,
                    Url = page.Url

                };

                if (page.Fields?.Count > 0)
                {
                    foreach (var field in page.Fields)
                    {
                        newP.Fields.Add(field.Key, field.Value);

                    }

                }

                if (page.WebParts?.Count > 0)
                {
                    newP.WebParts.AddRange(page.WebParts);

                }

                result = JsonConvert.SerializeObject(newP, Newtonsoft.Json.Formatting.Indented);

            }

            return result;

        } //GetPageContent

        private string GetPublishing(Publishing publishing)
        {
            string result = string.Empty;
            if (publishing != null)
            {
                Publishing newP = new Publishing()
                {
                    AutoCheckRequirements = publishing.AutoCheckRequirements

                };

                if (publishing.AvailableWebTemplates?.Count > 0)
                {
                    newP.AvailableWebTemplates.AddRange(publishing.AvailableWebTemplates);

                }

                if (publishing.DesignPackage != null)
                {
                    newP.DesignPackage = new DesignPackage()
                    {
                        DesignPackagePath = publishing.DesignPackage.DesignPackagePath,
                        MajorVersion = publishing.DesignPackage.MajorVersion,
                        MinorVersion = publishing.DesignPackage.MinorVersion,
                        PackageGuid = publishing.DesignPackage.PackageGuid,
                        PackageName = publishing.DesignPackage.PackageName

                    };

                }

                if (publishing.PageLayouts?.Count > 0)
                {
                    foreach (var pageLayout in publishing.PageLayouts)
                    {
                        PageLayout newPL = new PageLayout()
                        {
                            IsDefault = pageLayout.IsDefault,
                            Path = pageLayout.Path

                        };

                        newP.PageLayouts.Add(newPL);

                    }

                }

                result = JsonConvert.SerializeObject(newP, Newtonsoft.Json.Formatting.Indented);

            }

            return result;

        } //GetPublishing

        private string GetTermGroup(Guid termGroupId)
        {
            string result = string.Empty;
            if (EditingTemplate?.TermGroups != null)
            {
                TermGroup termGroup = EditingTemplate.TermGroups.Find(p => p.Id.CompareTo(termGroupId) == 0);
                if (termGroup != null)
                {
                    TermGroup newTG = new TermGroup()
                    {
                        Description = termGroup.Description,
                        Id = termGroup.Id,
                        Name = termGroup.Name,
                        SiteCollectionTermGroup = termGroup.SiteCollectionTermGroup

                    };

                    if (termGroup.Contributors?.Count > 0)
                    {
                        foreach (var user in termGroup.Contributors)
                        {
                            PnPModel.User newU = new PnPModel.User()
                            {
                                Name = user.Name

                            };

                            newTG.Contributors.Add(newU);

                        }

                    }

                    if (termGroup.Managers?.Count > 0)
                    {

                        foreach (var user in termGroup.Managers)
                        {
                            PnPModel.User newU = new PnPModel.User()
                            {
                                Name = user.Name

                            };

                            newTG.Managers.Add(newU);

                        }

                    }

                    result = JsonConvert.SerializeObject(newTG, Newtonsoft.Json.Formatting.Indented);

                }

            }

            return result;

        } //GetTermGroup

        private void SetTermsIn(TermCollection here, TermCollection terms)
        {
            if (terms?.Count > 0)
            {
                foreach(var term in terms)
                {
                    Term newT = new Term()
                    {
                        CustomSortOrder = term.CustomSortOrder,
                        Description = term.Description,
                        Id = term.Id,
                        IsAvailableForTagging = term.IsAvailableForTagging,
                        IsDeprecated = term.IsDeprecated,
                        IsReused = term.IsReused,
                        IsSourceTerm = term.IsSourceTerm,
                        Language = term.Language,
                        Name = term.Name,
                        Owner = term.Owner,
                        SourceTermId = term.SourceTermId

                    };

                    if (term.Labels?.Count > 0)
                    {
                        foreach(var label in term.Labels)
                        {
                            TermLabel newL = new TermLabel()
                            {
                                IsDefaultForLanguage = label.IsDefaultForLanguage,
                                Language = label.Language,
                                Value = label.Value

                            };

                            newT.Labels.Add(newL);

                        }

                    }

                    if (term.LocalProperties?.Count > 0)
                    {
                        foreach(var localProperty in term.LocalProperties)
                        {
                            newT.LocalProperties.Add(localProperty.Key, localProperty.Value);

                        }

                    }

                    if (term.Properties?.Count > 0)
                    {
                        foreach(var property in term.Properties)
                        {
                            newT.Properties.Add(property.Key, property.Value);

                        }

                    }

                    if (term.Terms?.Count > 0)
                    {
                        SetTermsIn(newT.Terms, term.Terms);

                    }

                    here.Add(newT);
                }

            }

        }

        private string GetTermSet(TermSet termSet)
        {
            string result = string.Empty;
            if (termSet != null)
            {
                TermSet newTS = new TermSet()
                {
                    Description = termSet.Description,
                    Id = termSet.Id,
                    IsAvailableForTagging = termSet.IsAvailableForTagging,
                    IsOpenForTermCreation = termSet.IsOpenForTermCreation,
                    Language = termSet.Language,
                    Name = termSet.Name,
                    Owner = termSet.Owner

                };

                if (termSet.Properties?.Count > 0)
                {
                    foreach(var keyValue in termSet.Properties)
                    {
                        newTS.Properties.Add(keyValue.Key, keyValue.Value);

                    }

                }

                SetTermsIn(newTS.Terms, termSet.Terms);

                result = JsonConvert.SerializeObject(newTS, Newtonsoft.Json.Formatting.Indented);

            }

            return result;

        } //GetTermSet

        public void SaveTemplateForEdit(TemplateItems templateItems)
        {
            if (EditingTemplate != null)
            {
                ProvisioningTemplate template = EditingTemplate;

                if (template.AddIns?.Count > 0)
                {
                    List<TemplateItem> deletedItems = templateItems.GetDeletedItems(TemplateItemType.AddInItem);
                    if (deletedItems?.Count > 0)
                    {
                        foreach (var templateItem in deletedItems)
                        {
                            template.AddIns.RemoveAll(p => p.PackagePath.Equals(templateItem.Name, StringComparison.OrdinalIgnoreCase));

                            templateItems.RemoveItem(templateItem);
                        }

                    }

                    List<TemplateItem> changedItems = templateItems.GetChangedItems(TemplateItemType.AddInItem);
                    if (changedItems?.Count > 0)
                    {
                        foreach (var templateItem in changedItems)
                        {
                            AddIn addIn = template.AddIns.Find(p => p.PackagePath.Equals(templateItem.Name, 
                                                                                         StringComparison.OrdinalIgnoreCase));
                            if (addIn != null)
                            {
                                addIn.Source = templateItem.Content as string;

                            }

                            templateItems.CommitItem(templateItem);

                        }

                    }

                } //if AddIns

                if (template.ComposedLook != null)
                {
                    List<TemplateItem> composedLookTemplateItems = templateItems.GetItems(TemplateItemType.ComposedLook);
                    if (composedLookTemplateItems?.Count > 0)
                    {
                        foreach (var templateItem in composedLookTemplateItems)
                        {
                            if (templateItem.IsDeleted)
                            {
                                template.ComposedLook = null;

                                templateItems.RemoveItem(templateItem);

                            }
                            else if (templateItem.IsChanged)
                            {
                                string[] values = templateItem.Content as string[];
                                template.ComposedLook.Name = values[(int)ComposedLookProperties.Name];
                                template.ComposedLook.BackgroundFile = values[(int)ComposedLookProperties.BackgroundFile];
                                template.ComposedLook.ColorFile = values[(int)ComposedLookProperties.ColorFile];
                                template.ComposedLook.FontFile = values[(int)ComposedLookProperties.FontFile];
                                template.ComposedLook.Version = Convert.ToInt32(values[(int)ComposedLookProperties.Version]);

                                templateItems.CommitItem(templateItem);

                            }

                        }

                    }

                } //if ComposedLook

                if (template.ContentTypes?.Count > 0)
                {
                    List<TemplateItem> deletedItems = templateItems.GetDeletedItems(TemplateItemType.ContentTypeItem);
                    if (deletedItems?.Count > 0)
                    {
                        foreach (var templateItem in deletedItems)
                        {
                            template.ContentTypes.RemoveAll(p => p.Id.Equals(templateItem.Name, StringComparison.OrdinalIgnoreCase));

                            templateItems.RemoveItem(templateItem);

                        }

                    }

                    List<TemplateItem> changedItems = templateItems.GetChangedItems(TemplateItemType.ContentTypeItem);
                    if (changedItems?.Count > 0)
                    {
                        foreach (var templateItem in changedItems)
                        {
                            PnPModel.ContentType oldCT = template.ContentTypes.Find(p => 
                                                            p.Id.Equals(templateItem.Name, StringComparison.OrdinalIgnoreCase));
                            if (oldCT != null)
                            {
                                string contentType = templateItem.Content as string;
                                PnPModel.ContentType newCT = JsonConvert.DeserializeObject<PnPModel.ContentType>(contentType);
                                template.ContentTypes.Remove(oldCT);
                                template.ContentTypes.Add(newCT);

                            }

                            templateItems.CommitItem(templateItem);

                        }

                    }

                } //if ContentTypes

                if (template.CustomActions?.SiteCustomActions?.Count > 0)
                {
                    List<TemplateItem> deletedItems = templateItems.GetDeletedItems(TemplateItemType.SiteCustomActionItem);
                    if (deletedItems?.Count > 0)
                    {
                        foreach(var templateItem in deletedItems)
                        {
                            template.CustomActions.SiteCustomActions.RemoveAll(p => 
                                p.RegistrationId.Equals(templateItem.Name, StringComparison.OrdinalIgnoreCase));

                            templateItems.RemoveItem(templateItem);

                        }

                    }

                    List<TemplateItem> changedItems = templateItems.GetChangedItems(TemplateItemType.SiteCustomActionItem);
                    if (changedItems?.Count > 0)
                    {
                        foreach(var templateItem in changedItems)
                        {
                            CustomAction oldCA = template.CustomActions.SiteCustomActions.Find(p => 
                                                    p.RegistrationId.Equals(templateItem.Name, StringComparison.OrdinalIgnoreCase));
                            if (oldCA != null)
                            {
                                string customAction = templateItem.Content as string;
                                CustomAction newCA = JsonConvert.DeserializeObject<CustomAction>(customAction);
                                template.CustomActions.SiteCustomActions.Remove(oldCA);
                                template.CustomActions.SiteCustomActions.Add(newCA);

                            }

                            templateItems.CommitItem(templateItem);

                        }

                    }

                } //if SiteCustomActions

                if (template.CustomActions?.WebCustomActions?.Count > 0)
                {
                    List<TemplateItem> deletedItems = templateItems.GetDeletedItems(TemplateItemType.WebCustomActionItem);
                    if (deletedItems?.Count > 0)
                    {
                        foreach (var templateItem in deletedItems)
                        {
                            template.CustomActions.WebCustomActions.RemoveAll(p => 
                                p.RegistrationId.Equals(templateItem.Name, StringComparison.OrdinalIgnoreCase));

                            templateItems.RemoveItem(templateItem);

                        }

                    }

                    List<TemplateItem> changedItems = templateItems.GetChangedItems(TemplateItemType.WebCustomActionItem);
                    if (changedItems?.Count > 0)
                    {
                        foreach (var templateItem in changedItems)
                        {
                            CustomAction oldCA = template.CustomActions.WebCustomActions.Find(p => 
                                                    p.RegistrationId.Equals(templateItem.Name, StringComparison.OrdinalIgnoreCase));
                            if (oldCA != null)
                            {
                                string customAction = templateItem.Content as string;
                                CustomAction newCA = JsonConvert.DeserializeObject<CustomAction>(customAction);
                                template.CustomActions.WebCustomActions.Remove(oldCA);
                                template.CustomActions.WebCustomActions.Add(newCA);

                            }

                            templateItems.CommitItem(templateItem);

                        }

                    }

                } //if WebCustomActions

                if (template.Features?.SiteFeatures?.Count > 0)
                {
                    List<TemplateItem> siteFeatureTemplateItems = templateItems.GetItems(TemplateItemType.SiteFeatureList);
                    if (siteFeatureTemplateItems?.Count > 0)
                    {
                        foreach (var templateItem in siteFeatureTemplateItems)
                        {
                            if (templateItem.IsDeleted)
                            {
                                template.Features.SiteFeatures.Clear();

                                templateItems.RemoveItem(templateItem);

                            }
                            else if (templateItem.IsChanged)
                            {
                                KeyValueList keyValueList = templateItem.Content as KeyValueList;
                                template.Features.SiteFeatures.Clear();
                                foreach(var keyValue in keyValueList)
                                {
                                    PnPModel.Feature feature = new PnPModel.Feature();
                                    feature.Id = new Guid(keyValue.Value);
                                    template.Features.SiteFeatures.Add(feature);
                                    templateItems.CommitItem(templateItem);

                                }

                            }

                        }

                    }

                } //if SiteFeatures

                if (template.Features?.WebFeatures?.Count > 0)
                {
                    List<TemplateItem> webFeatureTemplateItems = templateItems.GetItems(TemplateItemType.WebFeatureList);
                    if (webFeatureTemplateItems?.Count > 0)
                    {
                        foreach (var templateItem in webFeatureTemplateItems)
                        {
                            if (templateItem.IsDeleted)
                            {
                                template.Features.WebFeatures.Clear();

                                templateItems.RemoveItem(templateItem);

                            }
                            else if (templateItem.IsChanged)
                            {
                                template.Features.WebFeatures.Clear();
                                KeyValueList keyValueList = templateItem.Content as KeyValueList;
                                foreach (var keyValue in keyValueList)
                                {
                                    PnPModel.Feature feature = new PnPModel.Feature();
                                    feature.Id = new Guid(keyValue.Value);
                                    template.Features.WebFeatures.Add(feature);
                                    templateItems.CommitItem(templateItem);

                                }

                            }

                        }

                    }

                } //if SiteFeatures

                if (template.Files?.Count > 0)
                {
                    
                    List<TemplateItem> deletedItems = templateItems.GetDeletedItems(TemplateItemType.FileItem);
                    if (deletedItems?.Count > 0)
                    {
                        foreach(var templateItem in deletedItems)
                        {
                            PnPModel.File file = template.Files.Find(p => p.Src.Equals(templateItem.Name, 
                                                                                       StringComparison.OrdinalIgnoreCase));
                            template.Connector.DeleteFile(file.Src);
                            template.Files.Remove(file);

                            templateItems.RemoveItem(templateItem);

                        }

                    }

                    if (template.Files?.Count > 0)
                    {
                        deletedItems = templateItems.GetDeletedItems(TemplateItemType.FileWebPartItem);
                        if (deletedItems?.Count > 0)
                        {
                            foreach (var templateItem in deletedItems)
                            {
                                TemplateItem parentItem = templateItems.GetParent(templateItem, TemplateItemType.FileItem);
                                if (parentItem != null)
                                {
                                    PnPModel.File file = template.Files.Find(p => p.Src.Equals(parentItem.Name,
                                                                                               StringComparison.OrdinalIgnoreCase));
                                    if (file != null)
                                    {
                                        string[] titles = templateItem.Name.Split(new char[] { '_' });
                                        string webPartTitle = titles[titles.Length - 1];
                                        WebPart webPart = file.WebParts.Find(p => p.Title.Equals(webPartTitle,
                                                                                                 StringComparison.OrdinalIgnoreCase));
                                        if (webPart != null)
                                        {
                                            file.WebParts.Remove(webPart);
                                            templateItems.RemoveItem(templateItem);

                                        }

                                    }

                                }

                            }

                        }

                        deletedItems = templateItems.GetDeletedItems(TemplateItemType.FileWebPartItemContent);
                        if (deletedItems?.Count > 0)
                        {
                            foreach (var templateItem in deletedItems)
                            {
                                TemplateItem parentItem = templateItems.GetParent(templateItem, TemplateItemType.FileItem);
                                if (parentItem != null)
                                {
                                    PnPModel.File file = template.Files.Find(p => p.Src.Equals(parentItem.Name,
                                                                                               StringComparison.OrdinalIgnoreCase));
                                    if (file != null)
                                    {
                                        parentItem = templateItems.GetParent(templateItem);
                                        if (parentItem != null)
                                        {
                                            string[] titles = parentItem.Name.Split(new char[] { '_' });
                                            string webPartTitle = titles[titles.Length - 1];
                                            WebPart webPart = file.WebParts.Find(p => p.Title.Equals(webPartTitle,
                                                                                                     StringComparison.OrdinalIgnoreCase));
                                            if (webPart != null)
                                            {
                                                file.WebParts.Remove(webPart);
                                                templateItems.RemoveItem(parentItem);

                                            }

                                        }

                                    }

                                }

                            }

                        }

                    }

                    List<TemplateItem> changedItems = templateItems.GetChangedItems(TemplateItemType.FileItem);
                    if (changedItems?.Count > 0)
                    {
                        foreach (var templateItem in changedItems)
                        {
                            PnPModel.File oldFile = template.Files.Find(p => p.Src.Equals(templateItem.Name,
                                                                                       StringComparison.OrdinalIgnoreCase));
                            if (oldFile != null)
                            {
                                PnPModel.File newFile = JsonConvert.DeserializeObject<PnPModel.File>(templateItem.Content as string);

                                List<TemplateItem> children = templateItems.GetChildren(templateItem.Id); //Does this file have webparts?
                                if (children?.Count > 0)
                                {
                                    WebPartCollection webParts = new WebPartCollection(EditingTemplate);
                                    foreach(TemplateItem childItem in children)
                                    {
                                        List<TemplateItem> webPartItems = templateItems.GetChildren(childItem.Id);
                                        foreach(TemplateItem webPartItem in webPartItems)
                                        {
                                            WebPart webPart = JsonConvert.DeserializeObject<WebPart>(webPartItem.Content as string);
                                            List<TemplateItem> contentItems = templateItems.GetChildren(webPartItem.Id);
                                            foreach(TemplateItem contentItem in contentItems)
                                            {
                                                XElement element = XElement.Parse(contentItem.Content as string, LoadOptions.None);
                                                webPart.Contents = element.ToString(SaveOptions.DisableFormatting);

                                            }

                                            webParts.Add(webPart);

                                        }

                                    }

                                    if (webParts.Count > 0)
                                    {
                                        newFile.WebParts.AddRange(webParts);

                                    }

                                }

                                oldFile.Folder = newFile.Folder;
                                oldFile.Level = newFile.Level;
                                oldFile.Overwrite = newFile.Overwrite;
                                oldFile.Security = newFile.Security;
                                oldFile.Src = newFile.Src;
                                oldFile.WebParts.Clear();
                                oldFile.WebParts.AddRange(newFile.WebParts);
                                oldFile.Properties.Clear();
                                foreach(var keyValue in newFile.Properties)
                                {
                                    oldFile.Properties.Add(keyValue.Key, keyValue.Value);

                                }

                            }

                            templateItems.CommitItem(templateItem);
                            
                        }

                    } //if changedItems - files

                    changedItems = templateItems.GetChangedItems(TemplateItemType.FileWebPartItem);
                    if (changedItems?.Count > 0)
                    {
                        foreach(var templateItem in changedItems)
                        {
                            if (templateItem.IsChanged)
                            {
                                TemplateItem parentItem = templateItems.GetParent(templateItem, TemplateItemType.FileItem);
                                if (parentItem != null)
                                {
                                    PnPModel.File file = template.Files.Find(p => p.Src.Equals(parentItem.Name));
                                    if (file != null)
                                    {
                                        List<TemplateItem> children = templateItems.GetChildren(parentItem.Id);
                                        if (children?.Count > 0)
                                        {
                                            WebPartCollection webParts = new WebPartCollection(EditingTemplate);
                                            foreach (TemplateItem childItem in children)
                                            {
                                                List<TemplateItem> webPartItems = templateItems.GetChildren(childItem.Id);
                                                foreach (TemplateItem webPartItem in webPartItems)
                                                {
                                                    WebPart webPart = JsonConvert.DeserializeObject<WebPart>(webPartItem.Content as string);
                                                    List<TemplateItem> contentItems = templateItems.GetChildren(webPartItem.Id);
                                                    foreach (TemplateItem contentItem in contentItems)
                                                    {
                                                        XElement element = XElement.Parse(contentItem.Content as string, LoadOptions.None);
                                                        webPart.Contents = element.ToString(SaveOptions.DisableFormatting);

                                                    }

                                                    webParts.Add(webPart);

                                                }

                                            }

                                            if (webParts.Count > 0)
                                            {
                                                file.WebParts.Clear();
                                                file.WebParts.AddRange(webParts);

                                            }

                                        }

                                    }

                                    templateItems.CommitItem(parentItem);

                                }

                            }

                        }

                    } //if changedItems 2

                    changedItems = templateItems.GetChangedItems(TemplateItemType.FileWebPartItemContent);
                    if (changedItems?.Count > 0)
                    {
                        foreach (var templateItem in changedItems)
                        {
                            if (templateItem.IsChanged)
                            {
                                TemplateItem parentItem = templateItems.GetParent(templateItem, TemplateItemType.FileItem);
                                if (parentItem != null)
                                {
                                    PnPModel.File file = template.Files.Find(p => p.Src.Equals(parentItem.Name));
                                    if (file != null)
                                    {
                                        List<TemplateItem> children = templateItems.GetChildren(parentItem.Id);
                                        if (children?.Count > 0)
                                        {
                                            WebPartCollection webParts = new WebPartCollection(EditingTemplate);
                                            foreach (TemplateItem childItem in children)
                                            {
                                                List<TemplateItem> webPartItems = templateItems.GetChildren(childItem.Id);
                                                foreach (TemplateItem webPartItem in webPartItems)
                                                {
                                                    WebPart webPart = JsonConvert.DeserializeObject<WebPart>(webPartItem.Content as string);
                                                    List<TemplateItem> contentItems = templateItems.GetChildren(webPartItem.Id);
                                                    foreach (TemplateItem contentItem in contentItems)
                                                    {
                                                        XElement element = XElement.Parse(contentItem.Content as string, LoadOptions.None);
                                                        webPart.Contents = element.ToString(SaveOptions.DisableFormatting);

                                                    }

                                                    webParts.Add(webPart);

                                                }

                                            }

                                            if (webParts.Count > 0)
                                            {
                                                file.WebParts.Clear();
                                                file.WebParts.AddRange(webParts);

                                            }

                                        }

                                    }

                                    templateItems.CommitItem(parentItem);

                                }

                            }

                        }

                    } //if changedItems 3

                } //if Files


                if (template.Lists?.Count > 0)
                {
                    List<TemplateItem> deletedItems = templateItems.GetDeletedItems(TemplateItemType.ListItem);
                    if (deletedItems?.Count > 0)
                    {
                        foreach(var templateItem in deletedItems)
                        {
                            ListInstance listInstance = template.Lists.Find(p => p.Url.Equals(templateItem.Name, 
                                                                                              StringComparison.OrdinalIgnoreCase));
                            if (listInstance != null)
                            {
                                template.Lists.Remove(listInstance);

                                templateItems.RemoveItem(templateItem);

                            }

                        }

                    } //if deletedItems

                    deletedItems = templateItems.GetDeletedItems(TemplateItemType.ListFieldItem);
                    if (deletedItems?.Count > 0)
                    {
                        foreach(var templateItem in deletedItems)
                        {
                            TemplateItem parentItem = templateItems.GetParent(templateItem, TemplateItemType.ListItem);
                            if (parentItem != null)
                            {
                                ListInstance listInstance = template.Lists.Find(p => p.Url.Equals(parentItem.Name, 
                                                                                                  StringComparison.OrdinalIgnoreCase));
                                if (listInstance != null)
                                {
                                    foreach(var field in listInstance.Fields)
                                    {
                                        XElement element = XElement.Parse(field.SchemaXml, LoadOptions.None);
                                        string fieldName = element.Attribute("Name").Value;
                                        if(templateItem.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                                        {
                                            listInstance.Fields.Remove(field);
                                            templateItems.RemoveItem(templateItem);

                                            break;

                                        }

                                    }

                                }

                            }

                        }

                    } //if deletedItems - List Fields

                    deletedItems = templateItems.GetDeletedItems(TemplateItemType.ListViewItem);
                    if (deletedItems?.Count > 0)
                    {
                        foreach (var templateItem in deletedItems)
                        {
                            TemplateItem parentItem = templateItems.GetParent(templateItem, TemplateItemType.ListItem);
                            if (parentItem != null)
                            {
                                ListInstance listInstance = template.Lists.Find(p => p.Url.Equals(parentItem.Name,
                                                                                                  StringComparison.OrdinalIgnoreCase));
                                if (listInstance != null)
                                {
                                    foreach (var view in listInstance.Views)
                                    {
                                        XElement element = XElement.Parse(view.SchemaXml, LoadOptions.None);
                                        string viewName = element.Attribute("Name").Value;
                                        if (templateItem.Name.Equals(viewName, StringComparison.OrdinalIgnoreCase))
                                        {
                                            listInstance.Views.Remove(view);
                                            templateItems.RemoveItem(templateItem);

                                            break;

                                        }

                                    }

                                }

                            }

                        }

                    } //if deletedItems - List Views

                    List<TemplateItem> changedItems = templateItems.GetChangedItems(TemplateItemType.ListItem);
                    if (changedItems?.Count > 0)
                    {
                        foreach(var templateItem in changedItems)
                        {
                            ListInstance oldList = template.Lists.Find(p => p.Url.Equals(templateItem.Name, 
                                                                                         StringComparison.OrdinalIgnoreCase));
                            //

                        }

                    } //if changedItems



                } //if Lists

            } //if EditingTemplate

        } //SaveTemplateForEdit


    } //SharePoint2013OnPrem

} //namespace
