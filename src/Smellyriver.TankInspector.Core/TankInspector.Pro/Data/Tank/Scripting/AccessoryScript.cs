﻿using System.Globalization;

namespace Smellyriver.TankInspector.Pro.Data.Tank.Scripting
{
    class AccessoryScript : Script
    {
        public override int Priority
        {
            get { return 10; }
        }

        public static AccessoryScript Create(IXQueryable accessory)
        {
            if (accessory == null)
                return null;

            if (accessory["script/@name"] == "CamouflageNet")
                return new CamouflageNetScript(accessory);

            return new AccessoryScript(accessory);
        }

        private readonly IXQueryable _accessory;

        public IXQueryable Accessory
        {
            get { return _accessory; }
        }

        public AccessoryScript(IXQueryable accessory)
        {
            _accessory = accessory;
        }

        public override void Execute(ScriptingContext context)
        {
            var scriptName = this.Accessory["script/@name"];
            var domain = scriptName.Substring(0, 1).ToLower() + scriptName.Substring(1);
            if (domain == "staticFactorDevice" || domain == "staticAdditiveDevice")
            {
                double value;
                if (double.TryParse(this.Accessory["script/factor"], 
                                    NumberStyles.Float | NumberStyles.AllowThousands, 
                                    CultureInfo.InvariantCulture, 
                                    out value))
                {
                    var name = this.Accessory["script/attribute"].Replace('/', '_');
                    context.SetValue(domain, name, value);
                }
            }
            else
            {
                foreach (var attribute in this.Accessory.QueryMany("script/*"))
                {
                    double value;
                    if (double.TryParse(attribute.Value, 
                                        NumberStyles.Float | NumberStyles.AllowThousands,
                                        CultureInfo.InvariantCulture,
                                        out value))
                        context.SetValue(domain, attribute.Name, value);
                }
            }
        }


    }
}
