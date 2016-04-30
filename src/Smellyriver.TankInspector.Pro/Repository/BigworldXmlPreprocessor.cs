﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.XPath;
using Smellyriver.TankInspector.IO.XmlDecoding;
using Smellyriver.TankInspector.Pro.Repository.Versioning;
using Smellyriver.TankInspector.Pro.Repository.XmlProcessing;
using Smellyriver.TankInspector.Common.Utilities;

namespace Smellyriver.TankInspector.Pro.Repository
{
    internal partial class BigworldXmlPreprocessor
    {
        private static XElement LoadFile(string file)
        {
            using (var reader = new BigworldXmlReader(file))
            {
                var element = XElement.Load(reader);
                element.TrimText();

                return element;
            }
        }

        private readonly LocalGameClientPath _paths;
        private readonly LocalGameClientLocalization _localization;

        private readonly Lazy<XElement> _lazyCommonVehicleData;
        private XElement CommonVehicleData
        {
            get { return _lazyCommonVehicleData.Value; }
        }

        public BigworldXmlPreprocessor(LocalGameClientPath paths, LocalGameClientLocalization localization)
        {
            _paths = paths;
            _localization = localization;
            _lazyCommonVehicleData = new Lazy<XElement>(this.ProcessCommonVehicleDataFile);
        }

        public XElement ProcessTankListFile(string nation)
        {
            return BigworldXmlPreprocessor.LoadFile(_paths.GetTankListFile(nation))
                .TrimNameTail()
                .ProcessElements(e =>
                    {
                        e = e.NameToAttribute("tank")
                             .ProcessPriceElement()
                             .Select("tags", tags => tags.TextToElementList("tag"))
                             .LocalizeValue("userString", _localization)
                             .LocalizeValue("description", _localization)
                             .LocalizeValue("shortUserString", _localization);

                        if (e.Element("shortUserString") == null)
                        {
                            var userStringElement = e.Element("userString");
                            Debug.Assert(userStringElement != null, "userStringElement!=null");
                            e.Add(new XElement(userStringElement) { Name = "shortUserString" });
                        }

                        var tagsElement = e.Element("tags");
                        Debug.Assert(tagsElement != null, "tagsElement!=null");

                        var classTag = tagsElement.Elements().First();
                        var classKey = classTag.Value;
                        var className = _localization.GetLocalizedClassName(classKey);
                        var classElement = new XElement("class", className);
                        classElement.SetAttributeValue("key", classKey);
                        e.Add(classElement);

                        var nationElement = new XElement("nation", _localization.GetLocalizedNationName(nation));
                        nationElement.SetAttributeValue("key", nation);
                        e.Add(nationElement);
                    });
        }

        public XElement ProcessTankFile(string nation, string key)
        {
            var element = BigworldXmlPreprocessor.LoadFile(_paths.GetTankFile(nation, key))
                .TrimNameTail()
                .NameToAttribute("tank")
                .RenameElement("crew", "crews")
                .RenameElement("camouflage", "camouflageInfo")
                .ProcessElements("crews", e => e.NameToAttribute("crew", "role")
                                                .TextToElement("secondaryRoles")
                                                .Select("secondaryRoles", s => s.TextToElementList("secondaryRole")))
                .Select("hull", e => e.ProcessArmorList(this.CommonVehicleData))
                .ProcessTankModuleListNode("chassis", "chassis", _localization,
                    e => e.ProcessArmorList(this.CommonVehicleData)
                          .Select("terrainResistance", t => t.TextToElements("hard", "medium", "soft")))
                .RenameElement("turrets0", "turrets")
                .ProcessTankModuleListNode("turrets", "turret", _localization,
                    e => e.ProcessArmorList(this.CommonVehicleData)
                          .ProcessTankModuleListNode("guns", "gun", _localization, g => BigworldXmlPreprocessor.ProcessGunNode(g, this.CommonVehicleData)))
                .ProcessTankModuleListNode("engines", "engine", _localization, BigworldXmlPreprocessor.ProcessEngineNode)
                .ProcessTankModuleListNode("fuelTanks", "fuelTank", _localization)
                .ProcessTankModuleListNode("radios", "radio", _localization);

            var unlockNodes = element.XPathSelectElements("(chassis/chassis|turrets/turret|turrets/turret/guns/gun|engines/engine|radios/radio)/unlocks/*");
            foreach (var unlockNode in unlockNodes)
            {
                string searchRootPath;
                switch (unlockNode.Name.LocalName)
                {
                    case "gun":
                        searchRootPath = "turrets/turret/guns/gun";
                        break;
                    case "chassis":
                        searchRootPath = "chassis/chassis";
                        break;
                    case "turret":
                        searchRootPath = "turrets/turret";
                        break;
                    case "engine":
                        searchRootPath = "engines/engine";
                        break;
                    case "radio":
                        searchRootPath = "radios/radio";
                        break;
                    default:
                        continue;
                }

                var targetElements = element.XPathSelectElements(string.Format("{0}[@key='{1}']",
                                                                               searchRootPath,
                                                                               unlockNode.Attribute("key").Value));

                foreach (var targetElement in targetElements)
                {
                    var costElement = unlockNode.Element("cost");
                    Debug.Assert(costElement != null, "costElement != null");
                    targetElement.SetElementValue("experience", costElement.Value);
                }
            }

            return element;
        }

        private static void ProcessEngineNode(XElement engine)
        {
            engine.Select("tags", t => t.TextToElementList("tag"));
        }


        [AvailableFromVersion("9.9")]
        private static XElement CreatePitchLimitComponentElement(string name, XElement dataElement, double defaultValue)
        {
            var element = new XElement(name);
            var maxValue = defaultValue;

            if (dataElement != null)
            {
                var data = dataElement.Value;
                var values = data.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                var outputDataElement = new XElement("data");

                for (var i = 0; i < values.Length; i += 2)
                {
                    var angle = values[i];
                    var pitch = values[i + 1];
                    var pair = new XElement("pitchData");
                    pair.SetAttributeValue("angle", angle);
                    pair.SetAttributeValue("value", pitch);
                    outputDataElement.Add(pair);

                    var pitchValue = double.Parse(pitch);
                    if (Math.Abs(pitchValue) > Math.Abs(maxValue))
                        maxValue = pitchValue;
                }

                element.Add(outputDataElement);
            }

            element.SetAttributeValue("maximum", maxValue);

            return element;
        }

        private static void ProcessGunNode(XElement gun, XElement commonVehicleData)
        {
            gun.ProcessArmorList(commonVehicleData)
               .ProcessShellList()
               .Select("turretYawLimits", t => t.TextToElements("left", "right"));

            if (gun.Element("turretYawLimits") == null)
            {
                var turretYawLimitsElement = new XElement("turretYawLimits");
                turretYawLimitsElement.SetElementValue("left", -180);
                turretYawLimitsElement.SetElementValue("right", 180);
                gun.Add(turretYawLimitsElement);
            }

            var pitchLimitsElement = gun.Element("pitchLimits");

            if (pitchLimitsElement != null)
            {
                var minPitchElement = pitchLimitsElement.Element("minPitch");
                var maxPitchElement = pitchLimitsElement.Element("maxPitch");
                if (minPitchElement != null || maxPitchElement != null) // post 9.9
                {
                    var defaultElevation = 0.0;
                    var defaultDepression = 0.0;
                    var commonValueText = pitchLimitsElement.FirstNode as XText;
                    if (commonValueText != null)
                    {
                        var commonValues = commonValueText.Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        Debug.Assert(commonValues.Length == 2);
                        defaultElevation = double.Parse(commonValues[0]);
                        defaultDepression = double.Parse(commonValues[1]);
                    }

                    gun.Add(BigworldXmlPreprocessor.CreatePitchLimitComponentElement("elevation",
                                                                                     minPitchElement,
                                                                                     defaultElevation));

                    gun.Add(BigworldXmlPreprocessor.CreatePitchLimitComponentElement("depression",
                                                                                     maxPitchElement,
                                                                                     defaultDepression));
                    
                }
                else
                    throw new NotSupportedException("pre-9.9 format of gun pitch limitation is not supported any more");
            }

            var clip = gun.Element("clip");
            if (clip == null)
            {
                gun.Add(clip = new XElement("clip"));
                clip.SetElementValue("count", 1);
                clip.SetElementValue("rate", 0);
                clip.SetElementValue("reloadTime", 0);
            }
            else
            {
                var clipRateElement = clip.Element("rate");
                Debug.Assert(clipRateElement != null, "clipRateElement != null");

                clip.SetElementValue("reloadTime", 60.0 / double.Parse(clipRateElement.Value, CultureInfo.InvariantCulture));
            }

            var burst = gun.Element("burst");
            if (burst == null)
            {
                gun.Add(burst = new XElement("burst"));
                burst.SetElementValue("count", 1);
                burst.SetElementValue("rate", 0);
            }
        }



        public XElement ProcessChassisListFile(string nation)
        {
            return ProcessModuleListFile(_paths.GetChassisListFile(nation), "chassis");
        }

        public XElement ProcessEngineListFile(string nation)
        {
            return ProcessModuleListFile(_paths.GetEngineListFile(nation), "engine", BigworldXmlPreprocessor.ProcessEngineNode);
        }

        public XElement ProcessFuelTankListFile(string nation)
        {
            return ProcessModuleListFile(_paths.GetFuelTankListFile(nation), "fuelTank");
        }

        public XElement ProcessGunListFile(string nation)
        {
            return ProcessModuleListFile(_paths.GetGunListFile(nation), "gun", g => BigworldXmlPreprocessor.ProcessGunNode(g, this.CommonVehicleData));
        }

        public XElement ProcessTurretListFile(string nation)
        {
            return ProcessModuleListFile(_paths.GetTurretListFile(nation), "turret");
        }

        public XElement ProcessRadioListFile(string nation)
        {
            return ProcessModuleListFile(_paths.GetRadioListFile(nation), "radio");
        }

        private XElement ProcessModuleListFile(string file, string elementName, Action<XElement> additionalProcessing = null)
        {
            return BigworldXmlPreprocessor.LoadFile(file)
                .TrimNameTail()
                .ProcessElements("ids", e => e.NameToAttribute(elementName).TextToAttribute("id"))
                .ProcessElements("shared", e => e.NameToAttribute(elementName)
                                                 .ApplyProcessing(additionalProcessing)
                                                 .LocalizeValue("userString", _localization));
        }


        private XElement ProcessShellListFile(string nation)
        {
            return BigworldXmlPreprocessor.LoadFile(_paths.GetShellListFile(nation))
                .TrimNameTail()
                .RemoveElement("icons")
                .ProcessElements(e => e.NameToAttribute("shell")
                                        .ProcessPriceElement()
                                        .LocalizeValue("userString", _localization));

        }


        public XElement ProcessCommonVehicleDataFile()
        {
            return BigworldXmlPreprocessor.LoadFile(_paths.CommonVehicleDataFile)
                .TrimNameTail()
                .Select("balance", balance => balance.ProcessElements("byVehicleModule", e => e.Rename("tank").TextToAttribute("key"))
                                                     .Select("byComponentLevels", e => e.TextToElementList("weight")))
                .Select("materials", materials => materials.ProcessElements(e => e.NameToAttribute("material")));
        }

        public XElement ProcessEquipmentDataFile()
        {
            return this.ProcessAccessoryFile(_paths.EquipmentDataFile, "equipments", "equipment");
        }

        public XElement ProcessConsumableDataFile()
        {
            return this.ProcessAccessoryFile(_paths.ConsumableDataFile, "consumables", "consumable");
        }

        private XElement ProcessAccessoryFile(string file, string rootName, string itemName)
        {
            return BigworldXmlPreprocessor.LoadFile(file)
                .Rename(rootName)
                .ProcessElements(e =>
                    {
                        e.NameToAttribute(itemName)
                         .LocalizeValue("userString", _localization)
                         .LocalizeValue("description", _localization)
                         .ProcessPriceElement()
                         .Select("script", s => s.TextToAttribute("name"));

                        e.Select("vehicleFilter",
                            f =>
                            {
                                foreach (var element in f.XPathSelectElements("//tags"))
                                    element.TextToElementList("tag");

                                foreach (var element in f.XPathSelectElements("//nations"))
                                    element.TextToElementList("nation");
                            });
                    });
        }


        public XElement ProcessCommonCrewDataFile()
        {
            const string shortDescTag = "<shortDesc>";
            const string shortDescTagEnd = "</shortDesc>";

            return BigworldXmlPreprocessor.LoadFile(_paths.CommonCrewDataFile)
                .Rename("crews")
                .ProcessElements("roles", r => r.NameToAttribute("role")
                                                .LocalizeValue("userString", _localization)
                                                .LocalizeValue("description", _localization))
                .ProcessElements("skills", s =>
                {
                    s.LocalizeValue("userString", _localization)
                     .LocalizeValue("description", _localization);

                    var descriptionElement = s.Element("description");
                    Debug.Assert(descriptionElement != null, "descriptionElement != null");

                    var description = descriptionElement.Value;
                    var shortDescription = description;

                    var startIndex = description.IndexOf(shortDescTag, StringComparison.Ordinal);
                    var endIndex = description.IndexOf(shortDescTagEnd, StringComparison.Ordinal);
                    if (startIndex >= 0 && endIndex - startIndex - shortDescTag.Length > 0)
                    {
                        shortDescription = description.Substring(startIndex + shortDescTag.Length, endIndex - startIndex - shortDescTag.Length);
                        description = description.Substring(0, startIndex) + shortDescription + description.Substring(endIndex + shortDescTagEnd.Length);
                    }

                    descriptionElement.Value = description;
                    descriptionElement.AddAfterSelf(new XElement("shortDesc", shortDescription));


                    string role = null;
                    var name = s.Name.ToString();
                    var nameParts = name.Split('_');
                    if (nameParts.Length == 2)
                    {
                        role = nameParts[0];
                        name = nameParts[1];
                    }

                    s.Name = "skill";
                    s.SetAttributeValue("key", name);
                    if (role != null)
                        s.SetAttributeValue("role", role);
                });
        }

        public XElement ProcessCrewDefinitionFile(string nation)
        {
            var file = BigworldXmlPreprocessor.LoadFile(_paths.GetCrewDefinitionFile(nation))
                .TrimNameTail()
                .NameToAttribute("crewDefinition", "nation")
                .ProcessElements("ranks", r => r.LocalizeValue("userString", _localization));

            var ranks = file.Element("ranks");

            file.ProcessElements("roleRanks", r => r.NameToAttribute("ranks", "role")
                                                    .TextToElementList("rank")
                                                    .ProcessElements(e =>
                                                                     {
                                                                         var rank = e.Value.Trim();
                                                                         e.SetAttributeValue("key", rank);
                                                                         e.Value = ranks.Element(rank).Element("userString").Value;
                                                                     }));

            var men1 = file.XPathSelectElement("normalGroups/men1");
            file.Add(new XElement(men1.Element("firstNames"))
                         .ProcessElements(e => e.Rename("name")
                                                .LocalizeValue(_localization)));
            file.Add(new XElement(men1.Element("lastNames"))
                         .ProcessElements(e => e.Rename("name")
                                                .LocalizeValue(_localization)));
            file.Add(new XElement(men1.Element("icons"))
                         .Rename("portraits")
                         .ProcessElements(e => e.Rename("portrait")));

            file.RemoveElement("normalGroups");
            file.RemoveElement("premiumGroups");

            return file;
        }

        public XElement ProcessTechTreeLayoutFile(string nation)
        {
            try
            {
                var file = BigworldXmlPreprocessor.LoadFile(_paths.GetNationalTechTreeLayoutFile(nation))
                    .TrimNameTail()
                    .Select("grid", g => g.TextToAttribute("style"))
                    .ProcessElements("nodes", n => n.NameToAttribute("node", "key")
                                                    .ProcessElements("lines", l => l.NameToAttribute("line", "target")));

                file.Name = file.Name.LocalName.Substring(0, file.Name.LocalName.Length - "-tree".Length);
                file.NameToAttribute("layout", "nation");
                return file;
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        public XElement BuildTechTreeLayoutDatabase()
        {
            var result = new XElement("layouts");
            foreach (var nation in _paths.Nations)
            {
                var node = this.ProcessTechTreeLayoutFile(nation);
                if (node != null)
                    result.Add(node);
            }

            return result;

        }

        public XElement ProcessCustomizationFile(string nation)
        {
            var element = BigworldXmlPreprocessor.LoadFile(_paths.GetCustomizationFile(nation))
                .Rename("customization");
            element.SetAttributeValue("nation", nation);

            var inscriptionsElement = element.Element("inscriptions");
            var firstInscriptionElement = inscriptionsElement.Elements().First();
            firstInscriptionElement.Rename("inscription");


            firstInscriptionElement.LocalizeValue("userString", _localization);
            firstInscriptionElement.Element("inscriptions")
                                   .ProcessElements(e => e.LocalizeValue("userString", _localization)
                                                          .ElementToAttribute("id"));

            inscriptionsElement.ReplaceWith(firstInscriptionElement);

            element.Element("camouflageGroups")
                   .ProcessElements(e => e.LocalizeValue("userString", _localization)
                                          .NameToAttribute("camouflageGroup"));
            element.Element("camouflages")
                   .ProcessElements(e => e.LocalizeValue("description", _localization)
                                          .NameToAttribute("camouflage")
                                          .ElementToAttribute("id"));

            return element;
        }

        public XElement BuildCustomizationDatabase()
        {
            var result = new XElement("customizations");
            foreach (var nation in _paths.Nations)
            {
                var node = this.ProcessCustomizationFile(nation);
                if (node != null)
                    result.Add(node);
            }

            return result;
        }

        public XElement BuildCrewDatabase()
        {
            var result = new XElement("crews");

            var common = this.ProcessCommonCrewDataFile();
            common.ForEachElementStable(e =>
                {
                    e.Remove();
                    result.Add(e);
                });

            var crewDefinitions = new XElement("crewDefinitions");

            foreach (var nation in _paths.Nations)
            {
                var definition = this.ProcessCrewDefinitionFile(nation);
                crewDefinitions.Add(definition);
            }

            result.Add(crewDefinitions);

            return result;
        }

        public XElement BuildTankDatabase()
        {
            var result = new XElement("tanks");

            Parallel.ForEach(_paths.Nations, nation =>
                {
                    var nationFolder = _paths.GetNationFolder(nation);

                    var chassisFile = ProcessChassisListFile(nation);
                    var enginesFile = ProcessEngineListFile(nation);
                    var fuelTanksFile = ProcessFuelTankListFile(nation);
                    var gunsFile = ProcessGunListFile(nation);
                    var radiosFile = ProcessRadioListFile(nation);
                    var turretsFile = ProcessTurretListFile(nation);
                    var shellFile = ProcessShellListFile(nation);

                    var tankListFile = ProcessTankListFile(nation);

                    var successorInfo = new Dictionary<string, string[]>();
                    var successorInfoLock = new object();
                    var tankElements = new Dictionary<string, XElement>();
                    var tankElementsLock = new object();

                    Parallel.ForEach(tankListFile.Elements(), tank =>
                    {
                        var tankElement = new XElement(tank);
                        var tankKey = tank.Attribute("key").Value;
                        tankElement.SetAttributeValue("fullKey", RepositoryHelper.GetTankFullKeyEscaped(nation, tankKey));
                        tankElement.SetAttributeValue("iconKey", RepositoryHelper.GetTankHyphenKeyEscaped(nation, tankKey));

                        lock (tankElementsLock)
                        {
                            tankElements[tankKey] = tankElement;
                        }

                        var tankFile = ProcessTankFile(nation, tankKey);

                        var mmWeightElement = this.CommonVehicleData.Element("balance")?
                                                  .Element("byVehicleModule")?
                                                  .Elements()
                                                  .FirstOrDefault(e => e.Attribute("key").Value == tankKey);

                        if (mmWeightElement == null)
                        {
                            var tankTier = int.Parse(tank.Element("level").Value.Trim(), CultureInfo.InvariantCulture);
                            mmWeightElement = this.CommonVehicleData.Element("balance")?
                                                  .Element("byComponentLevels")?
                                                  .Elements()
                                                  .ElementAt(tankTier - 1);
                        }

                        tankElement.Add(new XElement("mmweight", mmWeightElement?.Value ?? "-1"));    // this value no long exists in (and maybe after) 9.15

                        foreach (var element in tankFile.Elements())
                        {
                            tankElement.Add(new XElement(element));
                        }

                        LinkTankModules(tankElement.Element("chassis"), chassisFile);
                        LinkTankModules(tankElement.Element("engines"), enginesFile);
                        LinkTankModules(tankElement.Element("fuelTanks"), fuelTanksFile);
                        LinkTankModules(tankElement.Element("radios"), radiosFile);
                        LinkTankModules(tankElement.Element("turrets"), turretsFile);
                        foreach (var turretElement in tankElement.Element("turrets").Elements())
                        {
                            LinkTankModules(turretElement.Element("guns"), gunsFile);
                            foreach (var gunElement in turretElement.Element("guns").Elements())
                            {
                                LinkShell(gunElement.Element("shots"), shellFile);
                            }
                        }

                        lock (successorInfoLock)
                        {
                            successorInfo.Add(tankKey, ((IEnumerable)tankFile.XPathEvaluate("//unlocks/vehicle/@key"))
                                                       .Cast<XAttribute>()
                                                       .Select(e => e.Value)
                                                       .ToArray());
                        }

                        result.Add(tankElement);
                    });

                    var predecessorInfo = new Dictionary<string, List<string>>();
                    foreach (var successorInfoItem in successorInfo)
                    {
                        foreach (var successor in successorInfoItem.Value)
                        {
                            var predecessorInfoItem = predecessorInfo.GetOrCreate(successor,
                                                                                  () => new List<string>());
                            predecessorInfoItem.Add(successorInfoItem.Key);
                        }
                    }

                    Parallel.ForEach(tankListFile.Elements(), tank =>
                    //foreach (var tank in tankListFile.Elements())
                    {
                        var tankKey = tank.Attribute("key").Value;
                        var tankElement = tankElements[tankKey];

                        var successorsElement = new XElement("successors");
                        string[] successors;
                        if (successorInfo.TryGetValue(tankKey, out successors))
                        {
                            foreach (var successor in successors)
                            {
                                var successorElement = new XElement("successor");
                                successorElement.SetAttributeValue("key", successor);
                                successorsElement.Add(successorElement);
                            }
                        }
                        tankElement.Add(successorsElement);

                        var predecessorsElement = new XElement("predecessors");
                        List<string> predecessors;
                        if (predecessorInfo.TryGetValue(tankKey, out predecessors))
                        {
                            foreach (var predecessor in predecessors)
                            {
                                var predecessorElement = new XElement("predecessor");
                                predecessorElement.SetAttributeValue("key", predecessor);
                                predecessorsElement.Add(predecessorElement);
                            }
                        }
                        tankElement.Add(predecessorsElement);
                    });
                });

            return result;
        }

        private void LinkTankModules(XElement list, XElement lookup)
        {
            var idsElement = lookup.Element("ids");
            var sharedElement = lookup.Element("shared");
            foreach (var elementVar in list.Elements().ToArray())
            {
                var element = elementVar;

                var key = element.Attribute("key").Value;

                var sharedAttribute = element.Attribute("shared");
                if (sharedAttribute != null && sharedAttribute.Value == "true")
                {
                    var sharedItemElement = sharedElement.Elements().Where(e => e.Attribute("key").Value == key).FirstOrDefault();
                    if (sharedItemElement != null)
                    {
                        var newElement = new XElement(sharedItemElement);

                        foreach (var child in element.Elements())
                            newElement.AddOrReplace(child);

                        foreach (var attribute in element.Attributes())
                            newElement.SetAttributeValue(attribute.Name, attribute.Value);

                        element.ReplaceWith(newElement);
                        element = newElement;
                    }
                }

                var idElement = idsElement.Elements().Where(e => e.Attribute("key").Value == key).FirstOrDefault();
                if (idElement != null)
                    element.Add(new XElement("id", idElement.Attribute("id").Value));

                //var sharedAttribute = element.Attribute("shared");
                //if (sharedAttribute != null && sharedAttribute.Value == "true")
                //{
                //    var sharedItemElement = sharedElement.Elements().Where(e => e.Attribute("key").Value == key).FirstOrDefault();
                //    if (sharedItemElement != null)
                //    {
                //        foreach (var childElement in sharedItemElement.Elements())
                //        {
                //            if (element.Element(childElement.Name) == null)
                //                element.Add(new XElement(childElement));
                //        }
                //    }
                //}
            }
        }

        private void LinkShell(XElement list, XElement lookup)
        {
            foreach (var element in list.Elements())
            {
                var key = element.Attribute("key").Value;

                var sharedItemElement = lookup.Elements().Where(e => e.Attribute("key").Value == key).FirstOrDefault();
                if (sharedItemElement != null)
                {
                    foreach (var childElement in sharedItemElement.Elements())
                    {
                        element.Add(new XElement(childElement));
                    }
                }
            }
        }

    }
}
