﻿//  Copyright 2015 Google Inc. All Rights Reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//  http ://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.

using HandleUtils;
using NDesk.Options;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace CheckDeviceAccess
{
    class Program
    {
        static bool _recursive;
        static int _pid;
        static bool _show_errors;
        static bool _identify_only;
        static bool _open_as_dir;
        static bool _filter_direct;

        static List<string> FindDeviceObjects(IEnumerable<string> names)
        {
            Queue<string> dumpList = new Queue<string>(names);
            HashSet<string> dumpedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> totalEntries = new List<string>();

            while (dumpList.Count > 0)
            {
                string name = dumpList.Dequeue();
                try
                {
                    ObjectDirectory directory = ObjectNamespace.OpenDirectory(name);

                    if (!dumpedDirs.Contains(directory.FullPath))
                    {
                        dumpedDirs.Add(directory.FullPath);
                        List<ObjectDirectoryEntry> sortedEntries = new List<ObjectDirectoryEntry>(directory.Entries);
                        sortedEntries.Sort();

                        string base_name = name.TrimEnd('\\');

                        IEnumerable<ObjectDirectoryEntry> objs = sortedEntries;

                        if (_recursive)
                        {
                            foreach (ObjectDirectoryEntry entry in sortedEntries.Where(d => d.IsDirectory))
                            {
                                dumpList.Enqueue(entry.FullPath);
                            }
                        }

                        totalEntries.AddRange(objs.Where(e => e.TypeName.Equals("device", StringComparison.OrdinalIgnoreCase)).Select(e => e.FullPath));    
                    }
                }
                catch (Win32Exception ex)
                {
                    if (ex.NativeErrorCode == 6)
                    {
                        // Add name in case it's an absolute name, not in a directory
                        totalEntries.Add(name);
                    }
                    else
                    {
                        Console.Error.WriteLine("Error querying {0} - {1}", name, ex.Message);
                    }
                }
            }

            return totalEntries;
        }

        static void ShowHelp(OptionSet p)
        {
            Console.WriteLine("Usage: CheckDeviceAccess [options] dir1 [dir2..dirN]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            p.WriteOptionDescriptions(Console.Out);
        }

        static bool CheckDevice(string name, bool writable)
        {
            bool success = false;

            try
            {
                using (ImpersonateProcess imp = NativeBridge.Impersonate(_pid, 
                    _identify_only ? TokenSecurityLevel.Identification : TokenSecurityLevel.Impersonate))
                {
                    uint access_mask = (uint)GenericAccessRights.GenericRead;
                    if (writable)
                    {
                        access_mask |= (uint)GenericAccessRights.GenericWrite;
                    }

                    FileOpenOptions opts = _open_as_dir ? FileOpenOptions.DIRECTORY_FILE : FileOpenOptions.NON_DIRECTORY_FILE;

                    using (NativeHandle handle = NativeBridge.CreateFileNative(name,
                        access_mask, 0, FileShareMode.All, FileCreateDisposition.Open,
                        opts))
                    {
                        success = true;
                    }
                }
            }
            catch (Win32Exception ex)
            {
                // Ignore access denied and invalid function (indicates there's no IRP_MJ_CREATE handler)
                if (_show_errors && (ex.NativeErrorCode != 5) && (ex.NativeErrorCode != 1))
                {
                    Console.Error.WriteLine("Error checking {0} - {1}", name, ex.Message);
                }
            }

            return success;
        }
        
        static string GetSymlinkTarget(ObjectDirectoryEntry entry)
        {
            try
            {
                return ObjectNamespace.ReadSymlink(entry.FullPath);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return "";
            }
        }

        static Dictionary<string, string> FindSymlinks()
        {
            Queue<string> dumpList = new Queue<string>(new string[] {"\\"});
            HashSet<string> dumpedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> symlinks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            while (dumpList.Count > 0)
            {
                string name = dumpList.Dequeue();
                try
                {
                    ObjectDirectory directory = ObjectNamespace.OpenDirectory(name);

                    if (!dumpedDirs.Contains(directory.FullPath))
                    {
                        dumpedDirs.Add(directory.FullPath);
                        List<ObjectDirectoryEntry> sortedEntries = new List<ObjectDirectoryEntry>(directory.Entries);
                        sortedEntries.Sort();

                        string base_name = name.TrimEnd('\\');

                        IEnumerable<ObjectDirectoryEntry> objs = sortedEntries;
                        
                        foreach (ObjectDirectoryEntry entry in sortedEntries.Where(d => d.IsDirectory))
                        {
                            dumpList.Enqueue(entry.FullPath);
                        }

                        foreach (ObjectDirectoryEntry entry in sortedEntries.Where(d => d.IsSymlink))
                        {
                            symlinks[GetSymlinkTarget(entry)] = entry.FullPath;
                        }
                    }
                }
                catch (Win32Exception)
                {
                }
            }

            return symlinks;
        }        

        static void DumpList(IEnumerable<string> names, bool map_to_symlink, Dictionary<string, string> symlinks)
        {
            int count = 0;
            foreach (string name in names)
            {
                count++;
                if (map_to_symlink && symlinks.ContainsKey(name))
                {
                    Console.WriteLine("{0} -> {1}", symlinks[name], name);
                }
                else
                {
                    Console.WriteLine(name);
                }
            }
            Console.WriteLine("Total Count: {0}", count);
        }

        static void Main(string[] args)
        {
            bool show_help = false;
            bool map_to_symlink = false;
            bool readable = false;
            string suffix = "XYZ";
            string namelist = null;

            _pid = Process.GetCurrentProcess().Id;

            try
            {
                OptionSet opts = new OptionSet() {
                        { "r", "Recursive tree directory listing",  
                            v => _recursive = v != null },          
                        { "l", "Try and map device names to a symlink", v => map_to_symlink = v != null },
                        { "p|pid=", "Specify a PID of a process to impersonate when checking", v => _pid = int.Parse(v.Trim()) },
                        { "suffix=", "Specify the suffix for the namespace search", v => suffix = v },
                        { "namelist=", "Specify a text file with a list of names", v => namelist = v },
                        { "e", "Display errors when trying devices, ignores Access Denied", v => _show_errors = v != null },
                        { "i", "Use an indentify level token when impersonating", v => _identify_only = v != null },
                        { "d", "Try opening devices as directories rather than files", v => _open_as_dir = v != null },
                        { "f", "Filter out devices which could be opened direct and via namespace", v => _filter_direct = v != null },
                        { "readonly", "Show devices which can be opened for read access instead of write", v => readable = v != null },
                        { "h|help",  "show this message and exit", 
                           v => show_help = v != null },
                    };

                List<string> names = opts.Parse(args);

                if (namelist != null)
                {
                    names.AddRange(File.ReadAllLines(namelist));
                }

                if (names.Count == 0 || show_help)
                {
                    ShowHelp(opts);
                }
                else
                {
                    List<string> device_objs = FindDeviceObjects(names);

                    if (device_objs.Count > 0)
                    {
                        List<string> write_normal = new List<string>(device_objs.Where(n => CheckDevice(n, !readable)));
                        List<string> write_namespace = new List<string>(device_objs.Where(n => CheckDevice(n + "\\" + suffix, !readable)));
                        
                        Dictionary<string, string> symlinks = FindSymlinks();                        

                        if (_filter_direct)
                        {
                            Console.WriteLine("Namespace Only");
                            HashSet<string> normal = new HashSet<string>(write_normal, StringComparer.OrdinalIgnoreCase);
                                                        
                            DumpList(write_namespace.Where(s => !normal.Contains(s)), map_to_symlink, symlinks);
                        }
                        else
                        {
                            Console.WriteLine("{0} Access", readable ? "Read" : "Write");
                            DumpList(write_normal, map_to_symlink, symlinks);
                            Console.WriteLine();
                            Console.WriteLine("{0} Access with Namespace", readable ? "Read" : "Write");
                            DumpList(write_namespace, map_to_symlink, symlinks);
                        }
                    }
                    else
                    {
                        Console.WriteLine("No device names specified");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }
    }
}
