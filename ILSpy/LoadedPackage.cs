﻿// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Reflection;

using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Metadata;

namespace ICSharpCode.ILSpy
{
	/// <summary>
	/// NuGet package or .NET bundle:
	/// </summary>
	public class LoadedPackage
	{
		public enum PackageKind
		{
			Zip,
			Bundle,
		}

		public PackageKind Kind { get; }

		/// <summary>
		/// List of all entries, including those in sub-directories within the package.
		/// </summary>
		public IReadOnlyList<PackageEntry> Entries { get; }

		internal IReadOnlyList<PackageEntry> TopLevelEntries { get; }
		internal IReadOnlyList<PackageFolder> TopLevelFolders { get; }

		public LoadedPackage(PackageKind kind, IEnumerable<PackageEntry> entries)
		{
			this.Kind = kind;
			this.Entries = entries.ToArray();
			var topLevelEntries = new List<PackageEntry>();
			var folders = new Dictionary<string, PackageFolder>();
			var rootFolder = new PackageFolder("");
			folders.Add("", rootFolder);
			foreach (var entry in this.Entries)
			{
				var (dirname, filename) = SplitName(entry.Name);
				GetFolder(dirname).Entries.Add(new FolderEntry(filename, entry));
			}
			this.TopLevelEntries = rootFolder.Entries;
			this.TopLevelFolders = rootFolder.Folders;

			static (string, string) SplitName(string filename)
			{
				int pos = filename.LastIndexOfAny(new char[] { '/', '\\' });
				if (pos == -1)
					return ("", filename); // file in root
				else
					return (filename.Substring(0, pos), filename.Substring(pos + 1));
			}

			PackageFolder GetFolder(string name)
			{
				if (folders.TryGetValue(name, out var result))
					return result;
				var (dirname, basename) = SplitName(name);
				PackageFolder parent = GetFolder(dirname);
				result = new PackageFolder(basename);
				parent.Folders.Add(result);
				folders.Add(name, result);
				return result;
			}
		}

		public static LoadedPackage FromZipFile(string file)
		{
			Debug.WriteLine($"LoadedPackage.FromZipFile({file})");
			using var archive = ZipFile.OpenRead(file);
			return new LoadedPackage(PackageKind.Zip,
				archive.Entries.Select(entry => new ZipFileEntry(file, entry)));
		}

		/// <summary>
		/// Load a .NET single-file bundle.
		/// </summary>
		public static LoadedPackage FromBundle(string fileName)
		{
			using var memoryMappedFile = MemoryMappedFile.CreateFromFile(fileName, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
			var view = memoryMappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
			try
			{
				if (!SingleFileBundle.IsBundle(view, out long bundleHeaderOffset))
					return null;
				var manifest = SingleFileBundle.ReadManifest(view, bundleHeaderOffset);
				var entries = manifest.Entries.Select(e => new BundleEntry(fileName, view, e)).ToList();
				var result = new LoadedPackage(PackageKind.Bundle, entries);
				view = null; // don't dispose the view, we're still using it in the bundle entries
				return result;
			}
			finally
			{
				view?.Dispose();
			}
		}

		/// <summary>
		/// Entry inside a package folder. Effectively renames the entry.
		/// </summary>
		sealed class FolderEntry : PackageEntry
		{
			readonly PackageEntry originalEntry;
			public override string Name { get; }

			public FolderEntry(string name, PackageEntry originalEntry)
			{
				this.Name = name;
				this.originalEntry = originalEntry;
			}

			public override ManifestResourceAttributes Attributes => originalEntry.Attributes;
			public override string FullName => originalEntry.FullName;
			public override ResourceType ResourceType => originalEntry.ResourceType;
			public override Stream TryOpenStream() => originalEntry.TryOpenStream();
		}

		sealed class ZipFileEntry : PackageEntry
		{
			readonly string zipFile;
			public override string Name { get; }
			public override string FullName => $"zip://{zipFile};{Name}";

			public ZipFileEntry(string zipFile, ZipArchiveEntry entry)
			{
				this.zipFile = zipFile;
				this.Name = entry.FullName;
			}

			public override Stream TryOpenStream()
			{
				Debug.WriteLine("Decompress " + Name);
				using var archive = ZipFile.OpenRead(zipFile);
				var entry = archive.GetEntry(Name);
				if (entry == null)
					return null;
				var memoryStream = new MemoryStream();
				using (var s = entry.Open())
				{
					s.CopyTo(memoryStream);
				}
				memoryStream.Position = 0;
				return memoryStream;
			}
		}

		sealed class BundleEntry : PackageEntry
		{
			readonly string bundleFile;
			readonly MemoryMappedViewAccessor view;
			readonly SingleFileBundle.Entry entry;

			public BundleEntry(string bundleFile, MemoryMappedViewAccessor view, SingleFileBundle.Entry entry)
			{
				this.bundleFile = bundleFile;
				this.view = view;
				this.entry = entry;
			}

			public override string Name => entry.RelativePath;
			public override string FullName => $"bundle://{bundleFile};{Name}";

			public override Stream TryOpenStream()
			{
				Debug.WriteLine("Open bundle member " + Name);
				return new UnmanagedMemoryStream(view.SafeMemoryMappedViewHandle, entry.Offset, entry.Size);
			}
		}
	}

	public abstract class PackageEntry : Resource
	{
		/// <summary>
		/// Gets the file name of the entry (may include path components, relative to the package root).
		/// </summary>
		public abstract override string Name { get; }

		/// <summary>
		/// Gets the full file name for the entry.
		/// </summary>
		public abstract string FullName { get; }
	}

	class PackageFolder
	{
		/// <summary>
		/// Gets the short name of the folder.
		/// </summary>
		public string Name { get; }

		public PackageFolder(string name)
		{
			this.Name = name;
		}

		public List<PackageFolder> Folders { get; } = new List<PackageFolder>();
		public List<PackageEntry> Entries { get; } = new List<PackageEntry>();
	}
}
