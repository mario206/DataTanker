﻿namespace DataTanker.PageManagement
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Diagnostics;
    using System.Linq;

    /// <summary>
    /// Implements a page-level interface of a storage
    /// over a file system.
    /// </summary>
    internal class FileSystemPageManager : IPageManager, IDisposable
    {
        private bool _disposed;

        private IStorage _storage;
        private readonly int _pageSize;
        private int _maxEmptyPages = 100;

        private readonly PageMap _pagemap;

        private static string _storageFileName = "storage";

        private object _locker = new object();

        private readonly bool _forcedWrites;
        private readonly int _writeBufferSize;

        // storage content file
        private FileStream _storageStream;

        private void Flush(Stream stream)
        {
            stream.Flush();
        }

        private void CheckIfFileExists(string fileName)
        {
            if (!File.Exists(fileName))
                throw new FileNotFoundException("File " + fileName + " not found");
        }

        private string StorageFileName()
        {
            return _storage.Path + Path.DirectorySeparatorChar + _storageFileName;
        }

        public void CheckIfStorageFilesExist(string path)
        {
            Lock();
            try
            {
                if (!Directory.Exists(path))
                    throw new DirectoryNotFoundException("Directory " + path + " not found");

                _pagemap.CheckIfFilesExist();

                CheckIfFileExists(StorageFileName());
            }
            finally
            {
                Unlock();
            }
        }

        private static void EnsureFileExists(string fileName)
        {
            if (File.Exists(fileName)) 
                return;

            using (File.Create(fileName))
            { }
        }

        [DebuggerNonUserCode]
        private void CheckDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException("FileSystemPageManager");
        }

        private void CheckStorage()
        {
            if (_storage == null)
                throw new InvalidOperationException("Storage is null");

            if (!_storage.IsOpen)
                throw new InvalidOperationException("Storage is not open");
        }

        private void CheckRemovalMarker(long pageIndex)
        {
            if (_pagemap.IsPageRemoved(pageIndex))
                throw new PageMapException("Page is marked as removed");
        }

        private IPage AppendPage()
        {
            long pageIndex = _pagemap.GetMaxPageIndex() + 1;

            _pagemap.WritePageAllocation(pageIndex, _storageStream.Length);
            _pagemap.WritePageIndex(pageIndex, _storageStream.Length);
            _pagemap.Flush();
            
            _storageStream.Seek(0, SeekOrigin.End);
            var pageBytes = new byte[_pageSize];
            _storageStream.Write(pageBytes, 0, _pageSize);
            Flush(_storageStream);

            var result = new Page(this, pageIndex, pageBytes);

            return result;
        }

        private IPage ResurrectPage()
        {
            long pageIndex = _pagemap.GetLastFreePageIndex();
            long pageAllocation = _pagemap.GetPageAllocation(pageIndex);

            if (pageAllocation == -1)
                throw new PageMapException("Page is removed");

            _pagemap.TruncateLastFreePageMarker();
            _pagemap.Flush();

            var pageBytes = new byte[_pageSize];
            _storageStream.Seek(pageAllocation, SeekOrigin.Begin);
            _storageStream.Write(pageBytes, 0, _pageSize);
            Flush(_storageStream);

            return new Page(this, pageIndex, pageBytes);
        }

        private void Vacuum()
        {
            // here we get all free page indexes and allocations
            var freePageIndexes = _pagemap.FreePageIndexes;

            var freePageData = freePageIndexes.Select(freePageIndex =>
                    new Tuple<long, long>(_pagemap.GetPageAllocation(freePageIndex), freePageIndex)).ToList();

            // sort allocations
            freePageData.Sort((p1, p2) => p1.Item1.CompareTo(p2.Item1));

            var reallocatedPageBytes = new byte[_pageSize];

            // reallocate pages at the end of storage file
            while (freePageData.Any())
            {
                long lastPageIndex = _pagemap.ReadLastPageIndex();

                // compute last page allocation
                long lastPageAllocation = _storageStream.Length - _pageSize;
                if (freePageData.All(t => t.Item1 != lastPageAllocation))
                {
                    // last page is occupied
                    // read its content
                    _storageStream.Seek(-_pageSize, SeekOrigin.End);
                    if (_storageStream.Read(reallocatedPageBytes, 0, _pageSize) < _pageSize)
                        throw new IOException("Unable to read reallocated page");

                    var firstFreePage = freePageData[0];
                    var newPageAllocation = firstFreePage.Item1;
                    freePageData.RemoveAt(0);

                    // write a page content to new place
                    _storageStream.Seek(newPageAllocation, SeekOrigin.Begin);
                    _storageStream.Write(reallocatedPageBytes, 0, _pageSize);

                    // write an allocation marker of reallocated page
                    _pagemap.WritePageAllocation(lastPageIndex, newPageAllocation);

                    // write an allocation marker of first free page 
                    _pagemap.WritePageAllocation(firstFreePage.Item2, -1);

                    // write a disk-to-virtual entry of reallocated page
                    _pagemap.WritePageIndex(lastPageIndex, newPageAllocation);
                }
                else
                {
                    // last page is empty
                    _pagemap.WritePageAllocation(lastPageIndex, -1);

                    freePageData.RemoveAll(t => t.Item1 == lastPageAllocation);
                }

                // truncate pagemap
                _pagemap.TruncateLastPageIndex();

                // truncate storage file
                _storageStream.SetLength(_storageStream.Length - _pageSize);
            }

            // clear free page indexes
            _pagemap.ClearFreePageMarkers();

            Flush(_storageStream);
            _pagemap.Flush();
        }

        /// <summary>
        /// Gets or sets a maximum number of empty (removed) pages in a storage file.
        /// When the number of empty pages exceeds this value, the vacuum 
        /// routine starts.
        /// </summary>
        public int MaxEmptyPages
        {
            get { return _maxEmptyPages; }
            set
            {
                if (value < 0)
                    throw new ArgumentException("Maximum empty pages should not be negative", "value");
                _maxEmptyPages = value;
            }
        }

        #region IPageManager Members

        /// <summary>
        /// Locks current instance.
        /// This should prevent an execution of another threads until Unlock() is called.
        /// </summary>
        public void Lock()
        {
            Monitor.Enter(_locker);
        }

        /// <summary>
        /// This should allow an execution of another threads.
        /// </summary>
        public void Unlock()
        {
            Monitor.Exit(_locker);
        }

        /// <summary>
        /// Determines if storage can be created
        /// </summary>
        /// <returns>True if storage can be created, false otherwise</returns>
        public bool CanCreate()
        {
            return _pagemap.CanCreate() && !File.Exists(StorageFileName());
        }

        /// <summary>
        /// Gets the storage instance that operates with storage pages via this page manager.
        /// </summary>
        public IStorage Storage
        {
            get { return _storage; }
            set { _storage = value; }
        }

        /// <summary>
        /// Checks if the page exists.
        /// </summary>
        /// <param name="pageIndex"></param>
        /// <returns></returns>
        public bool PageExists(long pageIndex)
        {
            if (pageIndex < 0)
                throw new ArgumentException("Page index should not be negative", "pageIndex");

            CheckDisposed();

            Lock();
            try
            {
                CheckStorage();

                if (pageIndex > _pagemap.GetMaxPageIndex())
                    return false;

                if (_pagemap.GetPageAllocation(pageIndex) == -1)
                    return false;

                if (_pagemap.IsPageRemoved(pageIndex)) 
                    return false;

                return true;
            }
            finally
            {
                Unlock();
            }
        }

        /// <summary>
        /// Fetches the page by index.
        /// </summary>
        /// <param name="pageIndex">An index of page</param>
        /// <returns>The fetched page</returns>
        public IPage FetchPage(long pageIndex)
        {
            CheckDisposed();

            Lock();
            try
            {
                CheckStorage();

                long pageAllocation = _pagemap.GetPageAllocation(pageIndex);
                if (pageAllocation == -1)
                    throw new PageMapException("Page is removed");

                CheckRemovalMarker(pageIndex);

                _storageStream.Seek(pageAllocation, SeekOrigin.Begin);
                var pageContent = new byte[_pageSize];
                if (_storageStream.Read(pageContent, 0, _pageSize) < _pageSize)
                    throw new IOException("Unable to read page content");

                return new Page(this, pageIndex, pageContent);
            }
            finally
            {
                Unlock();
            }
        }

        /// <summary>
        /// Fetches the page with specified index using provided array as a container.
        /// Callee should have full control over the sharing of content.
        /// This method should used to reduce buffers reallocation. 
        /// </summary>
        /// <param name="pageIndex">An index of page</param>
        /// <param name="content">The content of creating page</param>
        /// <returns>The fetched page</returns>
        public IPage FetchPage(long pageIndex, byte[] content)
        {
            CheckDisposed();

            if (content == null) throw new ArgumentNullException("content");
            if(content.Length != _pageSize)
                throw new ArgumentException("content");

            Lock();
            try
            {
                CheckStorage();

                long pageAllocation = _pagemap.GetPageAllocation(pageIndex);
                if (pageAllocation == -1)
                    throw new PageMapException("Page is removed");

                CheckRemovalMarker(pageIndex);

                _storageStream.Seek(pageAllocation, SeekOrigin.Begin);
                if (_storageStream.Read(content, 0, _pageSize) < _pageSize)
                    throw new IOException("Unable to read page content");

                return new Page(this, pageIndex, content);
            }
            finally
            {
                Unlock();
            }
        }

        /// <summary>
        /// Updates a page.
        /// </summary>
        /// <param name="page">A page instance to update</param>
        public void UpdatePage(IPage page)
        {
            CheckDisposed();

            if (page == null)
                throw new ArgumentNullException("page");

            Lock();
            try
            {
                CheckStorage();

                long pageAllocation = _pagemap.GetPageAllocation(page.Index);
                if (pageAllocation == -1)
                    throw new PageMapException("Page is removed");

                CheckRemovalMarker(page.Index);
                _storageStream.Seek(pageAllocation, SeekOrigin.Begin);
                _storageStream.Write(page.ContentCopy, 0, _pageSize);

                _pagemap.Flush();

                Flush(_storageStream);
            }
            finally 
            {
                Unlock();
            }
        }

        /// <summary>
        /// Creates a new page in storage.
        /// </summary>
        /// <returns>Created page</returns>
        public IPage CreatePage()
        {
            CheckDisposed();

            Lock();
            try
            {
                CheckStorage();
                return _pagemap.CanAppendPage() ? AppendPage() : ResurrectPage();
            }
            finally
            {
                Unlock();
            }
        }

        /// <summary>
        /// Removes a page from storage.
        /// </summary>
        /// <param name="pageIndex">Index of removing page</param>
        public void RemovePage(long pageIndex)
        {
            CheckDisposed();

            Lock();
            try
            {
                CheckStorage();

                if (pageIndex > _pagemap.GetMaxPageIndex())
                    throw new ArgumentException("Too large page index");

                long pageAllocation = _pagemap.GetPageAllocation(pageIndex);
                if (pageAllocation == -1)
                    throw new PageMapException("Page is removed");

                CheckRemovalMarker(pageIndex);

                _pagemap.MarkPageAsFree(pageIndex);

                if (_pagemap.GetEmptyPageCount() > _maxEmptyPages)
                    Vacuum();
                else
                    _pagemap.Flush();
            }
            finally
            {
                Unlock();
            }
        }

        /// <summary>
        /// Creates a new page space.
        /// </summary>
        public void CreateNewPageSpace()
        {
            CheckDisposed();

            Lock();
            try
            {
                if (!Directory.Exists(_storage.Path))
                    Directory.CreateDirectory(_storage.Path);

                if (File.Exists(StorageFileName()) || !_pagemap.CanCreate())
                {
                    throw new DataTankerException("Storage cannot be created here. Files with names matching the names of storage files already exist. Try to call OpenExisting()");
                }

                _pagemap.EnsureFilesExist();
                _pagemap.Create();

                EnsureFileExists(StorageFileName());

                OpenExistingPageSpace();
            }
            finally 
            {
                Unlock();
            }
        }

        /// <summary>
        /// Opens an existing page space.
        /// </summary>
        public void OpenExistingPageSpace()
        {
            CheckDisposed();

            Lock();
            try
            {
                FileOptions options = ForcedWrites ? FileOptions.WriteThrough : FileOptions.None;
                _storageStream = new FileStream(StorageFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.Read, _pageSize * _writeBufferSize, options);

                _pagemap.Open();
            }
            finally
            {
                Unlock();
            }
        }

        /// <summary>
        /// Closes the storage if it is open. Actually calls Dispose() method.
        /// </summary>
        public void Close()
        {
            Dispose();
        }

        /// <summary>
        /// Gets a page size in bytes.
        /// </summary>
        public int PageSize
        {
            get { return _pageSize; }
        }

        /// <summary>
        /// Gets the value indicating whether all write operations perform immediatly to file storage
        /// </summary>
        public bool ForcedWrites
        {
            get { return _forcedWrites; }
        }

        /// <summary>
        /// Checks if this page manager instance can
        /// operate with specified page.
        /// This is needed to properly creation of pages.
        /// </summary>
        /// <param name="page"></param>
        /// <returns></returns>
        public bool CheckPage(IPage page)
        {
            CheckDisposed();

            if (!(page is Page))
                return false;

            return page.Length == _pageSize;
        }

        #endregion

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    Lock();
                    try
                    {
                        if (_storageStream != null)
                            _storageStream.Close();

                        _pagemap.Dispose();
                    }
                    finally
                    {
                        Unlock();
                    }
                }
                _disposed = true;
            }
        }

        ~FileSystemPageManager()
        {
            Dispose(false);
        }

        /// <summary>
        /// Initializes a new instance of the FileSystemPageManager.
        /// </summary>
        /// <param name="pageSize"></param>
        internal FileSystemPageManager(int pageSize) 
            : this (pageSize, false, 100)
        {
        }

        /// <summary>
        /// Initializes a new instance of the FileSystemPageManager.
        /// </summary>
        /// <param name="pageSize"></param>
        /// <param name="forcedWrites"></param>
        /// <param name="writeBufferSize">The size of buffer (in pages) using to async write changes to disk. Async writing is not applied when forcedWrites is true.</param>
        internal FileSystemPageManager(int pageSize, bool forcedWrites, int writeBufferSize)
        {
            if (pageSize < 4096)
                throw new ArgumentOutOfRangeException("pageSize", "Too small page size");

            _pageSize = pageSize;
            _forcedWrites = forcedWrites;
            _writeBufferSize = forcedWrites || writeBufferSize < 1 ? 1 : writeBufferSize;

            _pagemap = new PageMap(this);
        }
    }
}