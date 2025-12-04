using System;
using System.IO;

namespace BrakeDiscInspector_GUI_ROI.Helpers
{
    public static class RecipePathHelper
    {
        public static string RecipesRoot => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recipes");

        public static string GetLayoutFolder(string layoutName)
            => Path.Combine(RecipesRoot, Sanitize(layoutName));

        public static string GetDatasetFolder(string layoutName)
            => Path.Combine(GetLayoutFolder(layoutName), "Dataset");

        public static string GetModelFolder(string layoutName)
            => Path.Combine(GetLayoutFolder(layoutName), "Model");

        public static string GetMasterFolder(string layoutName)
            => Path.Combine(GetLayoutFolder(layoutName), "Master");

        public static string GetObsoleteFolder(string baseFolder)
            => Path.Combine(baseFolder, "obsolete");

        public static void EnsureRecipeFolders(string layoutName)
        {
            Directory.CreateDirectory(GetDatasetFolder(layoutName));
            Directory.CreateDirectory(GetModelFolder(layoutName));
            Directory.CreateDirectory(GetMasterFolder(layoutName));
        }

        private static string Sanitize(string? layoutName)
        {
            if (string.IsNullOrWhiteSpace(layoutName))
            {
                return "DefaultLayout";
            }

            foreach (var c in Path.GetInvalidFileNameChars())
            {
                layoutName = layoutName.Replace(c, '_');
            }

            return layoutName;
        }
    }

    public static class ObsoleteFileHelper
    {
        public static void MoveExistingFilesToObsolete(string baseFolder, string searchPattern = "*.*")
        {
            if (!Directory.Exists(baseFolder))
            {
                return;
            }

            var obsoleteFolder = RecipePathHelper.GetObsoleteFolder(baseFolder);
            Directory.CreateDirectory(obsoleteFolder);

            foreach (var file in Directory.GetFiles(baseFolder, searchPattern))
            {
                var fileName = Path.GetFileName(file);
                var target = Path.Combine(obsoleteFolder, fileName);

                if (File.Exists(target))
                {
                    var stamped = Path.Combine(
                        obsoleteFolder,
                        Path.GetFileNameWithoutExtension(fileName)
                        + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                        + Path.GetExtension(fileName));
                    File.Move(file, stamped, overwrite: false);
                }
                else
                {
                    File.Move(file, target);
                }
            }
        }
    }
}
