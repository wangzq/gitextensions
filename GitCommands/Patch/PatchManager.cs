using System.Linq;
using GitCommands.Settings;
            if (body != null && module.EffectiveConfigFile.core.autocrlf.Value == AutoCRLFType.True)
            if (reset && body != null && module.EffectiveConfigFile.core.autocrlf.Value == AutoCRLFType.True)
            _patches = patchProcessor.CreatePatchesFromString(text).ToList();
                result = result.Combine("\n", Application.ProductName + " " + AppSettings.GitExtensionsVersionString);