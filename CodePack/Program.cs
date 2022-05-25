using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using System.IO;

namespace CodePack
{
    class Program
    {
        static string appRootFolder = "";
        static string[] gIncludePaths = null;
        static ILookup<string, string> gExceptFilePatterns = null;
        //static Dictionary<string, string[]> gExceptFilePatterns = null;

        static Dictionary<string, string[]> ScannedFiles = new Dictionary<string, string[]>();
        static Regex IncludeRegex = new Regex(@"^\s*\#include\s*""(?<path>[^""]+)""\s*$");
        static Regex IncludeSystemRegex = new Regex(@"^\s*\#include\s*\<(?<path>[^""]+)\>\s*$");

        static bool IsGlobalFolderExceptFiles(string folder, string sourceFile)
        {
            if (gExceptFilePatterns != null)
            {
                sourceFile = sourceFile.ToLower();
                if (gExceptFilePatterns.Contains(folder))
                {
                    var patterns = gExceptFilePatterns[folder];
                    foreach (var pattern in patterns)
                    {
                        if (sourceFile.IndexOf(pattern.ToLower()) != -1)
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        static string[] GetCppFiles(string folder)
        {
            return Directory
                .GetFiles(folder, "*.cpp", SearchOption.AllDirectories)
                .Where(file => !IsGlobalFolderExceptFiles(folder, file))
                .ToArray();
        }

        static string[] GetHeaderFiles(string folder)
        {
            return Directory
                .GetFiles(folder, "*.h", SearchOption.AllDirectories)
                .Where(file => !IsGlobalFolderExceptFiles(folder, file))
                .ToArray();
        }

        static string FindIncludeFile(string sourceFile, string includeFile)
        {
            foreach (var includePath in gIncludePaths)
            {
                string fullIncludeFile = Path.GetFullPath(includePath + @"\" + includeFile);
                if (File.Exists(fullIncludeFile))
                {
                    return fullIncludeFile;
                }
            }
            string localIncludeFile = Path.GetFullPath(Path.GetDirectoryName(sourceFile) + @"\" + includeFile);
            if (File.Exists(localIncludeFile))
                return localIncludeFile;
            else
                return null;
        }

        static string[] GetIncludedFiles(string sourceFile)
        {
            sourceFile = Path.GetFullPath(sourceFile);
            string[] result = null;
            if (!ScannedFiles.TryGetValue(sourceFile, out result))
            {
                List<string> directIncludeFiles = new List<string>();
                foreach (var line in File.ReadAllLines(sourceFile))
                {
                    Match match = IncludeRegex.Match(line);
                    if (match.Success)
                    {
                        string includeFile = match.Groups["path"].Value;
                        string fullIncludeFile = FindIncludeFile(sourceFile, includeFile);
                        if (fullIncludeFile != null && fullIncludeFile != "")
                        {
                            if (!directIncludeFiles.Contains(fullIncludeFile))
                            {
                                directIncludeFiles.Add(fullIncludeFile);
                            }
                        }
                        else
                        {
                            Console.WriteLine("Error: Header file not found.");
                            Console.WriteLine("Source File: '{0}', Included File: '{1}'",
                                              sourceFile, includeFile);
                            Console.WriteLine("");
                        }
                    }
                }

                for (int i = directIncludeFiles.Count - 1; i >= 0; i--)
                {
                    directIncludeFiles.InsertRange(i, GetIncludedFiles(directIncludeFiles[i]));
                }
                result = directIncludeFiles.Distinct().ToArray();
                ScannedFiles.Add(sourceFile, result);
            }
            return result;
        }

        static Dictionary<string, string[]> CategorizeSourceFiles(XDocument config, string[] files)
        {
            Dictionary<string, string[]> categorizedFiles = new Dictionary<string, string[]>();

            foreach (var e in config.Root.Element("categories").Elements("category"))
            {
                string name = e.Attribute("name").Value;
                string pattern = e.Attribute("pattern").Value.ToUpper();
                string[] exceptions = e.Elements("except").Select(x => x.Attribute("pattern").Value.ToUpper()).ToArray();
                string[] filteredFiles = files
                        .Where(filename =>
                        {
                            string path = filename.ToUpper();
                            return path.Contains(pattern) && exceptions.All(except => !path.Contains(except));
                        })
                        .ToArray();

                string[] previousFiles = null;
                if (categorizedFiles.TryGetValue(name, out previousFiles))
                {
                    filteredFiles = filteredFiles.Concat(previousFiles).ToArray();
                    categorizedFiles.Remove(name);
                }
                categorizedFiles.Add(name, filteredFiles);
            }

            foreach (var a in categorizedFiles.Keys)
            {
                foreach (var b in categorizedFiles.Keys)
                {
                    if (a != b)
                    {
                        if (categorizedFiles[a].Intersect(categorizedFiles[b]).Count() != 0)
                        {
                            throw new ArgumentException();
                        }
                    }
                }
            }

            return categorizedFiles;
        }

        static int GetSourceFileIndex(string sourcefile, string[] sourcefiles)
        {
            int index = 0;
            foreach (var file in sourcefiles)
            {
                if (file == sourcefile)
                    return index;
                index++;
            }
            return -1;
        }

        // Sort the categorize source files
        static Dictionary<string, string[]> SortCategorizeSourceFiles(Dictionary<string, string[]> categorizedFiles)
        {
            Dictionary<string, string[]> newCategorizedFiles = new Dictionary<string, string[]>();

            foreach (var cat in categorizedFiles.Keys)
            {
                string[] orderedFiles = categorizedFiles[cat];
                List<int> includeCounts = new List<int>();
                foreach (var sourceFile in orderedFiles)
                {
                    string[] includeFiles = null;
                    if (ScannedFiles.TryGetValue(sourceFile, out includeFiles))
                        includeCounts.Add(includeFiles.Count());
                    else
                        includeCounts.Add(0);
                }

                // Sort the categorize source file by include count [asc order]
                for (int i = 0; i < includeCounts.Count() - 1; i++)
                {
                    for (int j = i; j < includeCounts.Count(); j++)
                    {
                        if (includeCounts[i] > includeCounts[j])
                        {
                            int tmp = includeCounts[i];
                            includeCounts[i] = includeCounts[j];
                            includeCounts[j] = tmp;

                            string tmp_str = orderedFiles[i];
                            orderedFiles[i] = orderedFiles[j];
                            orderedFiles[j] = tmp_str;
                        }
                    }
                }

                string[] copyOrderedFiles = new string[orderedFiles.Count()];
                for (int i = 0; i < orderedFiles.Count(); i++)
                {
                    copyOrderedFiles[i] = orderedFiles[i];
                }

                // Sort the categorize source file by dependecies order [asc order]
                foreach (var sourceFile in copyOrderedFiles)
                {
                    string[] includeFiles = null;
                    if (ScannedFiles.TryGetValue(sourceFile, out includeFiles))
                    {
                        int sourceOrder = GetSourceFileIndex(sourceFile, orderedFiles);
                        foreach (var includeFile in includeFiles)
                        {
                            int includeOrder = GetSourceFileIndex(includeFile, orderedFiles);
                            if (includeOrder > sourceOrder)
                            {
                                string tmp = orderedFiles[includeOrder];
                                orderedFiles[includeOrder] = orderedFiles[sourceOrder];
                                orderedFiles[sourceOrder] = tmp;

                                sourceOrder = includeOrder;
                            }
                            else if (includeOrder == sourceOrder)
                            {
                                throw new ArgumentException();
                            }
                        }
                    }
                }

                newCategorizedFiles.Add(cat, orderedFiles);
            }
            return newCategorizedFiles;
        }

        static string[] SortDependecies(Dictionary<string, string[]> dependeicies)
        {
            var deps = dependeicies.ToDictionary(p => p.Key, p => new HashSet<string>(p.Value));
            List<string> sorted = new List<string>();
            while (deps.Count > 0)
            {
                bool found = false;
                foreach (var dep in deps)
                {
                    if (dep.Value.Count == 0)
                    {
                        found = true;
                        sorted.Add(dep.Key);
                        foreach (var d in deps.Values)
                        {
                            d.Remove(dep.Key);
                        }
                        deps.Remove(dep.Key);
                        break;
                    }
                }
                if (!found)
                {
                    throw new ArgumentException();
                }
            }
            return sorted.ToArray();
        }

        static string GetLongestCommonPrefix_old(string[] strings)
        {
            if (strings.Length == 0) return "";
            int shortestLength = strings.Select(s => s.Length).Min();
            return Enumerable.Range(0, shortestLength + 1)
                .Reverse()
                .Select(i => strings[0].Substring(0, i))
                .Where(s => strings.Skip(1).All(t => t.StartsWith(s)))
                .First();
        }

        static string GetLongestCommonPrefix(string[] strings)
        {
            if (strings.Length == 0) return "";
            int shortestLength = strings.Select(s => s.Length).Min();
            return Enumerable.Range(0, shortestLength + 1)
                .Reverse()
                .Select(i => strings[0].Substring(0, strings[0].Substring(0, i).LastIndexOf('\\') + 1))
                .Distinct()
                .Where(s => strings.Skip(1).All(t => t.StartsWith(s)))
                .First();
        }

        static void CombineFiles(string[] files, string outputFilename,
                                 HashSet<string> systemIncludes,
                                 Encoding encoding,
                                 params string[] externalIncludes)
        {
            try
            {
                string prefix = GetLongestCommonPrefix(files.Select(s => s.ToUpper()).ToArray());
                {
                    int index = prefix.LastIndexOf('/');
                    prefix = prefix.Substring(index + 1);
                }
                using (StreamWriter writer = new StreamWriter(new FileStream(outputFilename, FileMode.Create), encoding))
                {
                    writer.WriteLine("/**************************************************************************");
                    writer.WriteLine("  THIS FILE IS AUTOMATICALLY GENERATED BY CodePack. DO NOT MODIFY");
                    writer.WriteLine("  https://github.com/shines77/CodePack/ (Author: vczh & shines77)");
                    writer.WriteLine("**************************************************************************/");
                    foreach (var inc in externalIncludes)
                    {
                        writer.WriteLine("#include \"{0}\"", inc);
                    }

                    foreach (var file in files)
                    {
                        writer.WriteLine("");
                        writer.WriteLine("/**************************************************************************");
                        writer.WriteLine("  " + file.Substring(prefix.Length));
                        writer.WriteLine("**************************************************************************/");
                        foreach (var line in File.ReadAllLines(file, Encoding.Default))
                        {
                            Match match = null;

                            match = IncludeSystemRegex.Match(line);
                            if (match.Success)
                            {
                                if (systemIncludes.Add(match.Groups["path"].Value.ToUpper()))
                                {
                                    writer.WriteLine(line);
                                }
                            }
                            else
                            {
                                match = IncludeRegex.Match(line);
                                if (!match.Success)
                                {
                                    writer.WriteLine(line);
                                }
                            }
                        }
                    }
                }
                Console.WriteLine("Succeeded to write: {0}", outputFilename);
            }
            catch (Exception)
            {
                Console.WriteLine("Failed to write: {0}", outputFilename);
            }
        }

        static void CombineIncludeFiles(string inputFilename, string outputFilename,
                                        Encoding encoding, params string[] externalIncludes)
        {
            HashSet<string> systemIncludes = new HashSet<string>();
            string[] files = GetIncludedFiles(inputFilename).Concat(new string[] { inputFilename }).Distinct().ToArray();
            CombineFiles(files, outputFilename, systemIncludes, encoding, externalIncludes);
        }

        static Encoding TranslateEncoding(string outputEncoding)
        {
            Encoding encoding;
            outputEncoding = outputEncoding.ToLower();
            if (outputEncoding == "utf-8")
                encoding = Encoding.UTF8;
            else if(outputEncoding == "utf-7")
                encoding = Encoding.UTF7;
            else if (outputEncoding == "utf-32")
                encoding = Encoding.UTF32;
            else if (outputEncoding == "unicode")
                encoding = Encoding.Unicode;
            else if (outputEncoding == "ascii")
                encoding = Encoding.ASCII;
            else
                encoding = Encoding.Default;
            return encoding;
        }

        static void Main(string[] args)
        {
            // Print usage
            if (args.Length != 1)
            {
                Console.WriteLine("Usage:\n\n" +
                                  "  CodePack.exe <config-xml>\n");
                return;
            }

            // Load XML configuration
            XDocument config = XDocument.Load(args[0]);
            string folder = Path.GetDirectoryName(Path.GetFullPath(args[0])) + "\\";
            appRootFolder = folder;

            // The solution include paths
            gIncludePaths = config.Root
                .Element("include-paths")
                .Elements("folder")
                .Select(e => Path.GetFullPath(folder + e.Attribute("path").Value))
                .ToArray();

            // Get all the project files folder
            string[] folders = config.Root
                .Element("folders")
                .Elements("folder")
                .Select(e => Path.GetFullPath(folder + e.Attribute("path").Value))
                .ToArray();

            // Get all the except file patterns
            var exceptFilePatterns = config.Root
                .Element("folders")
                .Elements("folder")
                .Elements("except")
                .ToLookup(
                    e => Path.GetFullPath(folder + e.Parent.Attribute("path").Value),
                    e => e.Attribute("pattern").Value
                );

            // Transfer to global except file patterns
            gExceptFilePatterns = exceptFilePatterns;
            //gExceptFilePatterns = exceptFilePatterns.ToDictionary(group => group.Key, group => group.ToArray());

            // Collect the source files
            string[] unprocessedCppFiles = folders
                .SelectMany(GetCppFiles)
                .Distinct()
                .ToArray();

            // Collect the header files
            string[] unprocessedHeaderFiles = folders
                .SelectMany(GetHeaderFiles)
                .Distinct()
                .ToArray();

            // Search and append all included header files in all source files
            unprocessedHeaderFiles = folders
                .SelectMany(GetHeaderFiles)
                .Distinct()
                .Concat(unprocessedCppFiles)
                .SelectMany(GetIncludedFiles)
                .Concat(unprocessedHeaderFiles)
                .Distinct().ToArray();

            // The categorize code files
            var categorizedCppFiles = CategorizeSourceFiles(config, unprocessedCppFiles);
            var categorizedHeaderFiles = CategorizeSourceFiles(config, unprocessedHeaderFiles);

            categorizedHeaderFiles = SortCategorizeSourceFiles(categorizedHeaderFiles);

            var outputFolder = Path.GetFullPath(folder + config.Root.Element("output").Attribute("path").Value);
            var outputEncoding = TranslateEncoding(config.Root.Element("output").Attribute("encoding").Value);

            var categorizedOutput = config.Root
                .Element("output")
                .Elements("codepair")
                .ToDictionary(
                    e => e.Attribute("category").Value,
                    e => Tuple.Create(Path.GetFullPath(outputFolder + "\\" + e.Attribute("filename").Value),
                                      bool.Parse(e.Attribute("header-only").Value),
                                      bool.Parse(e.Attribute("generate").Value))
                    );

            // Calculate category dependencies
            var categoryDependencies = categorizedCppFiles
                .Keys
                .Select(cat =>
                {
                    var headerFiles = categorizedCppFiles[cat]
                        .SelectMany(GetIncludedFiles)
                        .Distinct()
                        .ToArray();
                    var keys = categorizedHeaderFiles
                        .Where(p => p.Value.Any(headFile => headerFiles.Contains(headFile)))
                        .Select(p => p.Key)
                        .Except(new string[] { cat })
                        .ToArray();
                    return Tuple.Create(cat, keys);
                })
                .ToDictionary(t => t.Item1, t => t.Item2);

            // Sort categories by dependencies
            var categoryOrder = SortDependecies(categoryDependencies);
            Dictionary<string, HashSet<string>> categorizedSystemIncludes = new Dictionary<string, HashSet<string>>();

            // Generate code pair header files
            foreach (var cat in categoryOrder)
            {
                string output = categorizedOutput[cat].Item1 + ".h";
                List<string> includes = new List<string>();
                foreach (var dep in categoryDependencies[cat])
                {
                    includes.AddRange(categorizedSystemIncludes[dep]);
                }
                HashSet<string> systemIncludes = new HashSet<string>(includes.Distinct());
                categorizedSystemIncludes.Add(cat, systemIncludes);
                if (categorizedOutput[cat].Item3)
                {
                    CombineFiles(
                        categorizedHeaderFiles[cat],
                        output,
                        systemIncludes,
                        outputEncoding,
                        categoryDependencies[cat]
                            .Select(dep => Path.GetFileName(categorizedOutput[dep].Item1 + ".h"))
                            .ToArray()
                        );
                }
            }

            // Generate code pair cpp files
            foreach (var cat in categoryOrder)
            {
                if (categorizedOutput[cat].Item3 && !categorizedOutput[cat].Item2)
                {
                    string output = categorizedOutput[cat].Item1;
                    string outputHeader = Path.GetFileName(output + ".h");
                    string outputCpp = output + ".cpp";
                    HashSet<string> systemIncludes = categorizedSystemIncludes[cat];
                    CombineFiles(
                        categorizedCppFiles[cat],
                        outputCpp,
                        systemIncludes,
                        outputEncoding,
                        outputHeader
                        );
                }
            }

            // Generate header files
            var headerOutput = config.Root
                .Element("output")
                .Elements("header")
                .ToDictionary(
                    e => Path.GetFullPath(folder + e.Attribute("source").Value),
                    e => Path.GetFullPath(outputFolder + e.Attribute("filename").Value)
                    );

            foreach (var obj in headerOutput)
            {
                CombineIncludeFiles(obj.Key, obj.Value + ".h", outputEncoding);
            }
        }
    }
}
