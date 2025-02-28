using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Sparrow;
using Sparrow.Platform;
using Sparrow.Server;
using Sparrow.Threading;
using Sparrow.Utils;
using Voron.Data;
using Voron.Data.BTrees;
using Voron.Data.CompactTrees;
using Voron.Data.Fixed;
using Voron.Data.Tables;
using Voron.Exceptions;
using Voron.Impl.FreeSpace;
using Voron.Impl.Journal;
using Voron.Impl.Paging;
using Voron.Impl.Scratch;
using Voron.Debugging;
using Voron.Util;
using Constants = Voron.Global.Constants;

namespace Voron.Impl
{
    public sealed unsafe class LowLevelTransaction : IPagerLevelTransactionState
    {
        public readonly AbstractPager DataPager;
        private readonly StorageEnvironment _env;
        private readonly long _id;
        private readonly ByteStringContext _allocator;
        internal readonly PageLocator _pageLocator;
        private bool _disposeAllocator;
        internal long DecompressedBufferBytes;
        internal TestingStuff _forTestingPurposes;

        public object ImmutableExternalState;

        private Tree _root;
        public Tree RootObjects => _root;

        public bool FlushedToJournal;

        private long _numberOfModifiedPages;

        public long NumberOfModifiedPages => _numberOfModifiedPages;

        private readonly WriteAheadJournal _journal;
        internal readonly List<JournalSnapshot> JournalSnapshots = new List<JournalSnapshot>();

        bool IPagerLevelTransactionState.IsWriteTransaction => Flags == TransactionFlags.ReadWrite;

        Dictionary<AbstractPager, TransactionState> IPagerLevelTransactionState.PagerTransactionState32Bits
        {
            get;
            set;
        }

        Dictionary<AbstractPager, CryptoTransactionState> IPagerLevelTransactionState.CryptoPagerTransactionState { get; set; }

        internal class WriteTransactionPool
        {
#if DEBUG
            public int BuilderUsages;
#endif
            public readonly TableValueBuilder TableValueBuilder = new TableValueBuilder();

            public int ScratchPagesTablePoolIndex = 0;
            public Dictionary<long, PageFromScratchBuffer> ScratchPagesInUse = new Dictionary<long, PageFromScratchBuffer>(NumericEqualityComparer.BoxedInstanceInt64);
            public Dictionary<long, PageFromScratchBuffer> ScratchPagesReadyForNextTx = new Dictionary<long, PageFromScratchBuffer>(NumericEqualityComparer.BoxedInstanceInt64);
            public readonly HashSet<long> DirtyPagesPool = new HashSet<long>(NumericEqualityComparer.BoxedInstanceInt64);

            public void Reset()
            {
                ScratchPagesReadyForNextTx.Clear();
                ScratchPagesInUse.Clear();
                DirtyPagesPool.Clear();
                TableValueBuilder.Reset();
            }
        }

        // BEGIN: Structures that are safe to pool.
        private readonly HashSet<long> _dirtyPages;
        private readonly Stack<long> _pagesToFreeOnCommit;
        private readonly Dictionary<long, PageFromScratchBuffer> _scratchPagesTable;
        private readonly HashSet<PagerState> _pagerStates;
        private readonly Dictionary<int, PagerState> _scratchPagerStates;
        // END: Structures that are safe to pool.


        public event Action<IPagerLevelTransactionState> BeforeCommitFinalization;

        public event Action<LowLevelTransaction> LastChanceToReadFromWriteTransactionBeforeCommit;

        public Size TransactionSize => new Size(NumberOfModifiedPages * Constants.Storage.PageSize, SizeUnit.Bytes) + AdditionalMemoryUsageSize;

        public Size AdditionalMemoryUsageSize
        {
            get
            {
                var cryptoTransactionStates = ((IPagerLevelTransactionState)this).CryptoPagerTransactionState;
                
                var total = DecompressedBufferBytes;

                if (cryptoTransactionStates != null)
                {
                    foreach (var state in cryptoTransactionStates.Values)
                    {
                        total += state.TotalCryptoBufferSize;
                    }
                }

                return new Size(total, SizeUnit.Bytes);
            }
        }
        public event Action<IPagerLevelTransactionState> OnDispose;
        
        /// <summary>
        /// This is called *under the write transaction lock* and will
        /// allow us to clean up any in memory state that shouldn't be preserved
        /// passed the transaction rollback
        /// </summary>
        public event Action<IPagerLevelTransactionState> OnRollBack;
        public event Action<LowLevelTransaction> AfterCommitWhenNewTransactionsPrevented;

        private readonly IFreeSpaceHandling _freeSpaceHandling;
        internal FixedSizeTree _freeSpaceTree;

        private TransactionHeader* _txHeader;

        private readonly HashSet<PageFromScratchBuffer> _transactionPages;
        private readonly HashSet<long> _freedPages;
        private readonly List<PageFromScratchBuffer> _unusedScratchPages;


        private readonly StorageEnvironmentState _state;

        private CommitStats _requestedCommitStats;
        private JournalFile.UpdatePageTranslationTableAndUnusedPagesAction? _updatePageTranslationTableAndUnusedPages;

        public TransactionPersistentContext PersistentContext { get; }
        public TransactionFlags Flags { get; }

        public bool IsLazyTransaction
        {
            get { return _isLazyTransaction; }
            set
            {
                _isLazyTransaction = value;
                if (_isLazyTransaction)
                    _env.Journal.HasLazyTransactions = true;
            }
        }

        public StorageEnvironment Environment => _env;

        public long Id => _id;

        public bool Committed { get; private set; }

        public bool RolledBack { get; private set; }

        public StorageEnvironmentState State => _state;

        public ByteStringContext Allocator => _allocator;

        public ulong Hash => _txHeader->Hash;

        public LowLevelTransaction(LowLevelTransaction previous, TransactionPersistentContext transactionPersistentContext, ByteStringContext allocator = null)
        {
            // this is used to clone a read transaction, so we can dispose the old one.
            // for example, in 32 bits, we may want to have a large transaction, but we 
            // can't keep around and scanning the whole db. So we clone the transaction,
            // create a new one, and release all the mappings associated with the old one
            // the new transaction will create its own mapping, using the existing page translation
            // table and install itself as the same transaction id as the current one. 

            Debug.Assert(previous.Flags == TransactionFlags.Read);

            IsCloned = true;

            PersistentContext = transactionPersistentContext;

            TxStartTime = previous.TxStartTime;
            DataPager = previous.DataPager;
            _env = previous._env;
            _journal = previous._journal;
            _id = previous._id;
            _freeSpaceHandling = previous._freeSpaceHandling;
            _allocator = allocator ?? new ByteStringContext(SharedMultipleUseFlag.None);
            _allocator.AllocationFailed += MarkTransactionAsFailed;
            _disposeAllocator = allocator == null;
            _pagerStates = new HashSet<PagerState>(ReferenceEqualityComparer<PagerState>.Default);

            Flags = TransactionFlags.Read;
            ImmutableExternalState = previous.ImmutableExternalState;

            try
            {
                CopyPagerStatesFromPreviousTx(previous);

                _pageLocator = transactionPersistentContext.AllocatePageLocator(this);

                _scratchPagerStates = previous._scratchPagerStates;

                _state = previous._state.Clone();

                InitializeRoots();

                JournalSnapshots = previous.JournalSnapshots;
            }
            catch
            {
                // need to restore ref counts of already added pager states

                foreach (var pagerState in _pagerStates)
                {
                    pagerState.Release();
                }

                throw;
            }

        }

        private LowLevelTransaction(LowLevelTransaction previous, TransactionPersistentContext persistentContext, long txId)
        {
            // this is meant to be used with transaction merging only
            // so it makes a lot of assumptions about the usage scenario
            // and what it can do

            Debug.Assert(previous.Flags == TransactionFlags.ReadWrite);

            var env = previous._env;
            env.Options.AssertNoCatastrophicFailure();

            Debug.Assert(env.Options.Encryption.IsEnabled == false,
                $"Async commit isn't supported in encrypted environments. We don't carry {nameof(IPagerLevelTransactionState.CryptoPagerTransactionState)} from previous tx");
            Debug.Assert((PlatformDetails.Is32Bits || env.Options.ForceUsing32BitsPager) == false,
                $"Async commit isn't supported in 32bits environments. We don't carry {nameof(IPagerLevelTransactionState.PagerTransactionState32Bits)} from previous tx");

            FlushInProgressLockTaken = previous.FlushInProgressLockTaken;
            CurrentTransactionHolder = previous.CurrentTransactionHolder;
            TxStartTime = DateTime.UtcNow;
            DataPager = env.Options.DataPager;
            _env = env;
            _journal = env.Journal;
            _id = txId;
            _freeSpaceHandling = previous._freeSpaceHandling;
            Debug.Assert(persistentContext != null, "persistentContext != null");
            PersistentContext = persistentContext;

            _allocator = new ByteStringContext(SharedMultipleUseFlag.None);
            _disposeAllocator = true;
            _allocator.AllocationFailed += MarkTransactionAsFailed;

            Flags = TransactionFlags.ReadWrite;

            _pagerStates = new HashSet<PagerState>(ReferenceEqualityComparer<PagerState>.Default);

            JournalFiles = previous.JournalFiles;

            foreach (var journalFile in JournalFiles)
            {
                journalFile.AddRef();
            }

            try
            {
                CopyPagerStatesFromPreviousTx(previous);

                EnsureNoDuplicateTransactionId(_id);

                // we can reuse those instances, not calling Reset on the pool
                // because we are going to need to scratch buffer pool

                _scratchPagesTable = _env.WriteTransactionPool.ScratchPagesReadyForNextTx;

                foreach (var kvp in previous._scratchPagesTable)
                {
                    if (previous._dirtyPages.Contains(kvp.Key))
                        _scratchPagesTable.Add(kvp.Key, kvp.Value);
                }
                previous._scratchPagesTable.Clear();
                _env.WriteTransactionPool.ScratchPagesInUse = _scratchPagesTable;
                _env.WriteTransactionPool.ScratchPagesReadyForNextTx = previous._scratchPagesTable;

                _dirtyPages = previous._dirtyPages;
                _dirtyPages.Clear();

                _freedPages = new HashSet<long>(NumericEqualityComparer.BoxedInstanceInt64);
                _unusedScratchPages = new List<PageFromScratchBuffer>();
                _transactionPages = new HashSet<PageFromScratchBuffer>(PageFromScratchBufferEqualityComparer.Instance);
                _pagesToFreeOnCommit = new Stack<long>();

                _state = previous._state.Clone();

                _pageLocator = PersistentContext.AllocatePageLocator(this);
                InitializeRoots();
                InitTransactionHeader();
            }
            catch
            {
                // need to restore ref counts of already added pager states

                foreach (var pagerState in _pagerStates)
                {
                    pagerState.Release();
                }

                throw;
            }
        }

        private void CopyPagerStatesFromPreviousTx(LowLevelTransaction previous)
        {
            var pagers = new HashSet<AbstractPager>();

            foreach (var scratchOrDataPagerState in previous._pagerStates)
            {
                // in order to avoid "dragging" pager state ref on non active scratch - we will not copy disposed scratches from previous async tx. RavenDB-6766
                if (scratchOrDataPagerState.DiscardOnTxCopy)
                    continue;

                // copy the "current pager" which is the last pager used, and by that do not "drag" old non used pager state refs to the next async commit (i.e. older views of data file). RavenDB-6949
                var currentPager = scratchOrDataPagerState.CurrentPager;
                if (pagers.Add(currentPager) == false)
                    continue;

                var pagerState = scratchOrDataPagerState;

                EnsurePagerStateReference(ref pagerState);
            }
        }

        public LowLevelTransaction(StorageEnvironment env, long id, TransactionPersistentContext transactionPersistentContext, TransactionFlags flags, IFreeSpaceHandling freeSpaceHandling, ByteStringContext context = null)
        {
            TxStartTime = DateTime.UtcNow;

            if (flags == TransactionFlags.ReadWrite)
                env.Options.AssertNoCatastrophicFailure();

            DataPager = env.Options.DataPager;
            _env = env;
            _journal = env.Journal;
            _id = id;
            _freeSpaceHandling = freeSpaceHandling;
            _allocator = context ?? new ByteStringContext(SharedMultipleUseFlag.None);

            _allocator.AllocationFailed += MarkTransactionAsFailed;

            _disposeAllocator = context == null;
            _pagerStates = new HashSet<PagerState>(ReferenceEqualityComparer<PagerState>.Default);

            PersistentContext = transactionPersistentContext;
            Flags = flags;

            var scratchPagerStates = env.ScratchBufferPool.GetPagerStatesOfAllScratches();

            try
            {
                foreach (var scratchOrDataPagerState in scratchPagerStates.Values)
                {
                    var pagerState = scratchOrDataPagerState;

                    EnsurePagerStateReference(ref pagerState);
                }

                _pageLocator = transactionPersistentContext.AllocatePageLocator(this);

                if (flags != TransactionFlags.ReadWrite)
                {
                    // for read transactions, we need to keep the pager state frozen
                    // for write transactions, we can use the current one (which == null)
                    _scratchPagerStates = scratchPagerStates;

                    _state = env.State.Clone();

                    InitializeRoots();

                    JournalSnapshots = _journal.GetSnapshots();

                    return;
                }

                EnsureNoDuplicateTransactionId(id);
                // we keep this copy to make sure that if we use async commit, we have a stable copy of the jounrals
                // as they were at the time we started the original transaction, this is required because async commit
                // may modify the list of files we have available
                JournalFiles = _journal.Files;
                foreach (var journalFile in JournalFiles)
                {
                    journalFile.AddRef();
                }
                _env.WriteTransactionPool.Reset();
                _scratchPagesTable = _env.WriteTransactionPool.ScratchPagesInUse;
                _dirtyPages = _env.WriteTransactionPool.DirtyPagesPool;
                _freedPages = new HashSet<long>(NumericEqualityComparer.BoxedInstanceInt64);
                _unusedScratchPages = new List<PageFromScratchBuffer>();
                _transactionPages = new HashSet<PageFromScratchBuffer>(PageFromScratchBufferEqualityComparer.Instance);
                _pagesToFreeOnCommit = new Stack<long>();

                _state = env.State.Clone();
                InitializeRoots();
                InitTransactionHeader();
            }
            catch
            {
                // need to restore ref counts of already added pager states

                foreach (var pagerState in _pagerStates)
                {
                    pagerState.Release();
                }

                throw;
            }
        }

        [Conditional("DEBUG")]
        private void EnsureNoDuplicateTransactionId(long id)
        {
            EnsureNoDuplicateTransactionId_Forced(id);
        }

        internal void EnsureNoDuplicateTransactionId_Forced(long id)
        {
            foreach (var journalFile in _journal.Files)
            {
                var lastSeenTxIdByJournal = journalFile.PageTranslationTable.GetLastSeenTransactionId();

                if (id <= lastSeenTxIdByJournal)
                    VoronUnrecoverableErrorException.Raise(this,
                        $"PTT of journal {journalFile.Number} already contains records for a new write tx. " +
                        $"Tx id = {id}, last seen by journal = {lastSeenTxIdByJournal}");

                if (journalFile.PageTranslationTable.IsEmpty)
                    continue;

                var maxTxIdInJournal = journalFile.PageTranslationTable.MaxTransactionId();

                if (id <= maxTxIdInJournal)
                    VoronUnrecoverableErrorException.Raise(this,
                        $"PTT of journal {journalFile.Number} already contains records for a new write tx. " +
                        $"Tx id = {id}, max id in journal = {maxTxIdInJournal}");
            }
        }

        internal void UpdateRootsIfNeeded(Tree root)
        {
            //can only happen during initial transaction that creates Root and FreeSpaceRoot trees
            if (State.Root != null)
                return;

            State.Root = root.State;

            _root = root;
        }

        private void InitializeRoots()
        {
            if (_state.Root != null)
            {
                _root = new Tree(this, null, Constants.RootTreeNameSlice, _state.Root);
            }
        }

        private void InitTransactionHeader()
        {
            Allocator.Allocate(sizeof(TransactionHeader), out _txHeaderMemory);
            Memory.Set(_txHeaderMemory.Ptr, 0, sizeof(TransactionHeader));

            _txHeader = (TransactionHeader*)_txHeaderMemory.Ptr;
            _txHeader->HeaderMarker = Constants.TransactionHeaderMarker;

            if (_id > 1 && _state.NextPageNumber <= 1)
                ThrowNextPageNumberCannotBeSmallerOrEqualThanOne();

            _txHeader->TransactionId = _id;
            _txHeader->NextPageNumber = _state.NextPageNumber;
            _txHeader->LastPageNumber = -1;
            _txHeader->PageCount = -1;
            _txHeader->Hash = 0;
            _txHeader->TimeStampTicksUtc = DateTime.UtcNow.Ticks;
            _txHeader->TxMarker = TransactionMarker.None;
            _txHeader->CompressedSize = 0;
            _txHeader->UncompressedSize = 0;
        }

        internal HashSet<PageFromScratchBuffer> GetTransactionPages()
        {
            VerifyNoDuplicateScratchPages();
            return _transactionPages;
        }

        internal List<PageFromScratchBuffer> GetUnusedScratchPages()
        {
            return _unusedScratchPages;
        }

        internal HashSet<long> GetFreedPagesNumbers()
        {
            return _freedPages;
        }

        public bool IsDirtyPage(long p) => _dirtyPages.Contains(p);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Page ModifyPage(long num)
        {
            _env.Options.AssertNoCatastrophicFailure();

            if (_pageLocator.TryGetWritablePage(num, out Page result))
                return result;

            return ModifyPageInternal(num);
        }

        private Page ModifyPageInternal(long num)
        {
            Page currentPage = GetPage(num);

            // Check if we can hit the second level locality cache.            
            if (_dirtyPages.Contains(num))
                return currentPage;

            // No cache hits.
            int pageSize;
            Page newPage;
            if (currentPage.IsOverflow)
            {
                newPage = AllocateOverflowRawPage(currentPage.OverflowSize, out var numberOfAllocatedPages, num, currentPage, zeroPage: false);
                pageSize = Constants.Storage.PageSize * numberOfAllocatedPages;
            }
            else
            {
                newPage = AllocatePage(1, num, currentPage, zeroPage: false); // allocate new page in a log file but with the same number			
                pageSize = Environment.Options.PageSize;
            }

            Memory.Copy(newPage.Pointer, currentPage.Pointer, pageSize);

            ZeroPageHeaderChecksumToEnsureNoUseOfCryptoReservedSpace(newPage.Pointer);

            TrackWritablePage(newPage);

            _pageLocator.SetWritable(num, newPage);

            return newPage;
        }

        [Conditional("DEBUG")]
        private void ZeroPageHeaderChecksumToEnsureNoUseOfCryptoReservedSpace(byte* page)
        {
            if (_env.Options.Encryption.IsEnabled)
            {
                // we don't want to mark all pages as changed by the crypto pager
                // that check for the full page hash, and it isn't relevant for 
                // crypto anyway
                return;
            }

            // in order to ensure that the last 32 bytes of the page are always reserved
            // for crypto, we'll zero them after copying them from the pager. If using 
            // encrypted, we'll always re-write it anyway, and this ensures that we'll
            // get corruption from the system if we try to use it. Note that the normal
            // pager stuff, like ensuring the checksum, is already done at this point
            Sodium.sodium_memzero(page + PageHeader.NonceOffset, (UIntPtr)(PageHeader.SizeOf - PageHeader.NonceOffset));
        }

        private const int InvalidScratchFile = -1;
        private PagerStateCacheItem _lastScratchFileUsed = new PagerStateCacheItem(InvalidScratchFile, null);
        private TxState _txState;

        [Flags]
        private enum TxState
        {
            None,
            Disposed = 1,
            Errored = 2
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Page GetPage(long pageNumber)
        {
            if (_txState != TxState.None)
                ThrowObjectDisposed();

            if (_pageLocator.TryGetReadOnlyPage(pageNumber, out Page result))
                return result;

            var p = GetPageInternal(pageNumber);

            _pageLocator.SetReadable(p.PageNumber, p);

            return p;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Page GetPageWithoutCache(long pageNumber)
        {
            if (_txState != TxState.None)
                ThrowObjectDisposed();

            return GetPageInternal(pageNumber);
        }

        private Page GetPageInternal(long pageNumber)
        {
            // Check if we can hit the lowest level locality cache.
            Page p;
            PageFromScratchBuffer value;
            if (_scratchPagesTable != null && _scratchPagesTable.TryGetValue(pageNumber, out value)) // Scratch Pages Table will be null in read transactions
            {
                Debug.Assert(value != null);
                PagerState state = null;
                if (_scratchPagerStates != null)
                {
                    var lastUsed = _lastScratchFileUsed;
                    if (lastUsed.FileNumber == value.ScratchFileNumber)
                    {
                        state = lastUsed.State;
                    }
                    else
                    {
                        state = _scratchPagerStates[value.ScratchFileNumber];
                        _lastScratchFileUsed = new PagerStateCacheItem(value.ScratchFileNumber, state);
                    }
                }

                p = _env.ScratchBufferPool.ReadPage(this, value.ScratchFileNumber, value.PositionInScratchBuffer, state);
                Debug.Assert(p.PageNumber == pageNumber, string.Format("Requested ReadOnly page #{0}. Got #{1} from scratch", pageNumber, p.PageNumber));
            }
            else
            {
                var pageFromJournal = _journal.ReadPage(this, pageNumber, _scratchPagerStates);
                if (pageFromJournal != null)
                {
                    p = pageFromJournal.Value;
                    Debug.Assert(p.PageNumber == pageNumber, string.Format("Requested ReadOnly page #{0}. Got #{1} from journal", pageNumber, p.PageNumber));
                }
                else
                {
                    p = new Page(DataPager.AcquirePagePointerWithOverflowHandling(this, pageNumber));

                    Debug.Assert(p.PageNumber == pageNumber, string.Format("Requested ReadOnly page #{0}. Got #{1} from data file", pageNumber, p.PageNumber));

                    // When encryption is off, we do validation by checksum
                    if (_env.Options.Encryption.IsEnabled == false)
                        _env.ValidatePageChecksum(pageNumber, (PageHeader*)p.Pointer);
                }
            }

            TrackReadOnlyPage(p);

            return p;
        }

        public T GetPageHeaderForDebug<T>(long pageNumber) where T : unmanaged
        {
            if (_txState != TxState.None)
                ThrowObjectDisposed();

            if (_pageLocator.TryGetReadOnlyPage(pageNumber, out Page page))
                return *(T*)page.Pointer;

            T result;
            PageFromScratchBuffer value;
            if (_scratchPagesTable != null && _scratchPagesTable.TryGetValue(pageNumber, out value)) // Scratch Pages Table will be null in read transactions
            {
                Debug.Assert(value != null);
                PagerState state = null;
                if (_scratchPagerStates != null)
                {
                    var lastUsed = _lastScratchFileUsed;
                    if (lastUsed.FileNumber == value.ScratchFileNumber)
                    {
                        state = lastUsed.State;
                    }
                    else
                    {
                        state = _scratchPagerStates[value.ScratchFileNumber];
                        _lastScratchFileUsed = new PagerStateCacheItem(value.ScratchFileNumber, state);
                    }
                }

                result = _env.ScratchBufferPool.ReadPageHeaderForDebug<T>(this, value.ScratchFileNumber, value.PositionInScratchBuffer, state);
            }
            else
            {
                var pageFromJournal = _journal.ReadPageHeaderForDebug<T>(this, pageNumber, _scratchPagerStates);
                if (pageFromJournal != null)
                {
                    result = pageFromJournal.Value;
                }
                else
                {
                    result = DataPager.AcquirePagePointerHeaderForDebug<T>(this, pageNumber);
                }
            }

            return result;
        }

        public void TryReleasePage(long pageNumber)
        {
            if (_scratchPagesTable != null && _scratchPagesTable.TryGetValue(pageNumber, out _))
            {
                // we don't release pages from the scratch buffers
                return;
            }

            var pageFromJournalExists = _journal.PageExists(this, pageNumber);
            if (pageFromJournalExists)
            {
                // we don't release pages from the scratch buffers that we found through the journals
                return;
            }

            DataPager.TryReleasePage(this, pageNumber);
        }

        private void ThrowObjectDisposed()
        {
            if (_txState.HasFlag(TxState.Disposed))
                throw new ObjectDisposedException("Transaction is already disposed");

            if (_txState.HasFlag(TxState.Errored))
                throw new InvalidDataException("The transaction is in error state, and cannot be used further");

            throw new ObjectDisposedException("Transaction state is invalid: " + _txState);
        }

        public Page AllocatePage(int numberOfPages, long? pageNumber = null, Page? previousPage = null, bool zeroPage = true)
        {
            if (pageNumber == null)
            {
                pageNumber = _freeSpaceHandling.TryAllocateFromFreeSpace(this, numberOfPages);
                if (pageNumber == null) // allocate from end of file
                {
                    pageNumber = State.NextPageNumber;
                    State.NextPageNumber += numberOfPages;
                }
            }
            return AllocatePage(numberOfPages, pageNumber.Value, previousPage, zeroPage);
        }

        public Page AllocateOverflowRawPage(long overflowSize, out int numberOfPages, long? pageNumber = null, Page? previousPage = null, bool zeroPage = true)
        {
            if (overflowSize > int.MaxValue - 1)
                throw new InvalidOperationException($"Cannot allocate chunks bigger than { int.MaxValue / 1024 * 1024 } Mb.");

            Debug.Assert(overflowSize >= 0);

            numberOfPages = VirtualPagerLegacyExtensions.GetNumberOfOverflowPages(overflowSize);

            var overflowPage = AllocatePage(numberOfPages, pageNumber, previousPage, zeroPage);
            overflowPage.Flags = PageFlags.Overflow;
            overflowPage.OverflowSize = (int)overflowSize;

            return overflowPage;
        }

        private Page AllocatePage(int numberOfPages, long pageNumber, Page? previousVersion, bool zeroPage)
        {
            if (_txState != TxState.None)
                ThrowObjectDisposed();

            try
            {
                var maxAvailablePageNumber = _env.Options.MaxStorageSize / Constants.Storage.PageSize;

                if (pageNumber > maxAvailablePageNumber)
                    ThrowQuotaExceededException(pageNumber, maxAvailablePageNumber);


                Debug.Assert(pageNumber < State.NextPageNumber);

#if VALIDATE
            VerifyNoDuplicateScratchPages();
#endif
                var pageFromScratchBuffer = _env.ScratchBufferPool.Allocate(this, numberOfPages);
                pageFromScratchBuffer.PreviousVersion = previousVersion;
                _transactionPages.Add(pageFromScratchBuffer);

                _numberOfModifiedPages += numberOfPages;

                _scratchPagesTable[pageNumber] = pageFromScratchBuffer;

                _dirtyPages.Add(pageNumber);

                TrackDirtyPage(pageNumber);

                if (numberOfPages != 1)
                {
                    _env.ScratchBufferPool.EnsureMapped(this,
                        pageFromScratchBuffer.ScratchFileNumber,
                        pageFromScratchBuffer.PositionInScratchBuffer,
                        numberOfPages);
                }

                var newPagePointer = _env.ScratchBufferPool.AcquirePagePointerForNewPage(this, pageFromScratchBuffer.ScratchFileNumber,
                    pageFromScratchBuffer.PositionInScratchBuffer, numberOfPages);

                if (zeroPage)
                    Memory.Set(newPagePointer, 0, Constants.Storage.PageSize * numberOfPages);

                var newPage = new Page(newPagePointer)
                {
                    PageNumber = pageNumber,
                    Flags = PageFlags.Single
                };

                _pageLocator.SetWritable(pageNumber, newPage);

                TrackWritablePage(newPage);

#if VALIDATE
            VerifyNoDuplicateScratchPages();
#endif

                return newPage;
            }
            catch
            {
                _txState |= TxState.Errored;
                throw;
            }
        }

        private void ThrowQuotaExceededException(long pageNumber, long? maxAvailablePageNumber)
        {
            throw new QuotaException(
                string.Format(
                    "The maximum storage size quota ({0} bytes) has been reached. " +
                    "Currently configured storage quota is allowing to allocate the following maximum page number {1}, while the requested page number is {2}. " +
                    "To increase the quota, use the MaxStorageSize property on the storage environment options.",
                    _env.Options.MaxStorageSize, maxAvailablePageNumber, pageNumber));
        }

        internal void BreakLargeAllocationToSeparatePages(long pageNumber)
        {
            if (_txState != TxState.None)
                ThrowObjectDisposed();

            PageFromScratchBuffer value;
            if (_scratchPagesTable.TryGetValue(pageNumber, out value) == false)
                throw new InvalidOperationException("The page " + pageNumber + " was not previous allocated in this transaction");

            if (value.NumberOfPages == 1)
                return;

            _transactionPages.Remove(value);
            _env.ScratchBufferPool.BreakLargeAllocationToSeparatePages(this, value);

            for (int i = 0; i < value.NumberOfPages; i++)
            {
                var pageFromScratchBuffer = new PageFromScratchBuffer(value.ScratchFileNumber, value.PositionInScratchBuffer + i, 1, 1);
                _transactionPages.Add(pageFromScratchBuffer);
                _scratchPagesTable[pageNumber + i] = pageFromScratchBuffer;
                _dirtyPages.Add(pageNumber + i);
                TrackDirtyPage(pageNumber + i);
                var newPage = _env.ScratchBufferPool.ReadPage(this, value.ScratchFileNumber, value.PositionInScratchBuffer + i);
                newPage.PageNumber = pageNumber + i;
                newPage.Flags = PageFlags.Single;
                TrackWritablePage(newPage);
            }
        }

        internal void ShrinkOverflowPage(long pageNumber, int newSize, TreeMutableState treeState)
        {
            if (_txState != TxState.None)
                ThrowObjectDisposed();

            PageFromScratchBuffer value;
            if (_scratchPagesTable.TryGetValue(pageNumber, out value) == false)
                throw new InvalidOperationException("The page " + pageNumber + " was not previous allocated in this transaction");

            var page = _env.ScratchBufferPool.ReadPage(this, value.ScratchFileNumber, value.PositionInScratchBuffer);
            if (page.IsOverflow == false || page.OverflowSize < newSize)
                throw new InvalidOperationException("The page " + pageNumber +
                                                    " was is not an overflow page greater than " + newSize);

            var prevNumberOfPages = VirtualPagerLegacyExtensions.GetNumberOfOverflowPages(page.OverflowSize);
            page.OverflowSize = newSize;
            var lowerNumberOfPages = VirtualPagerLegacyExtensions.GetNumberOfOverflowPages(newSize);

            Debug.Assert(lowerNumberOfPages != 0);

            if (prevNumberOfPages == lowerNumberOfPages)
                return;

            for (int i = lowerNumberOfPages; i < prevNumberOfPages; i++)
            {
                FreePage(page.PageNumber + i);
            }

            // need to set the proper number of pages in the scratch page

            var shrinked = _env.ScratchBufferPool.ShrinkOverflowPage(value, lowerNumberOfPages);

            _scratchPagesTable[pageNumber] = shrinked;
            _transactionPages.Remove(value);
            _transactionPages.Add(shrinked);

            treeState.OverflowPages -= prevNumberOfPages - lowerNumberOfPages;
        }

        [Conditional("DEBUG")]
        public void VerifyNoDuplicateScratchPages()
        {
            var pageNums = new HashSet<long>();
            foreach (var txPage in _transactionPages)
            {
                var scratchPage = Environment.ScratchBufferPool.ReadPage(this, txPage.ScratchFileNumber,
                    txPage.PositionInScratchBuffer);
                if (pageNums.Add(scratchPage.PageNumber) == false)
                    throw new InvalidDataException("Duplicate page in transaction: " + scratchPage.PageNumber);
            }
        }


        public bool IsValid => _txState == TxState.None;
        
        public bool IsDisposed => _txState.HasFlag(TxState.Disposed);

        public NativeMemory.ThreadStats CurrentTransactionHolder { get; set; }

        public void Dispose()
        {
            if (_txState.HasFlag(TxState.Disposed))
                return;

            try
            {
                if (!Committed && !RolledBack && Flags == TransactionFlags.ReadWrite)
                    Rollback();

                _txState |= TxState.Disposed;

                PersistentContext.FreePageLocator(_pageLocator);
            }
            finally
            {
                _env.TransactionCompleted(this);

                foreach (var pagerState in _pagerStates)
                {
                    pagerState.Release();
                }

                if (JournalFiles != null)
                {
                    foreach (var journalFile in JournalFiles)
                    {
                        journalFile.Release();
                    }
                }

                _root?.Dispose();
                _freeSpaceTree?.Dispose();

                _allocator.AllocationFailed -= MarkTransactionAsFailed;
              
                if (_disposeAllocator)
                {
                    if (_env.Options.Encryption.IsEnabled)
                    {
                        _allocator.Wipe();
                    }
                    _allocator.Dispose();
                }

                OnDispose?.Invoke(this);

                ImmutableExternalState = null;
            }
        }

        public void MarkTransactionAsFailed()
        {
            _txState |= TxState.Errored;
        }

        internal void FreePageOnCommit(long pageNumber)
        {
            _pagesToFreeOnCommit.Push(pageNumber);
        }

        internal void DiscardScratchModificationOn(long pageNumber)
        {
            if (_scratchPagesTable.TryGetValue(pageNumber, out var scratchPage))
            {
                if (_transactionPages.Remove(scratchPage))
                    _unusedScratchPages.Add(scratchPage);

                _scratchPagesTable.Remove(pageNumber);

                if (_env.Options.Encryption.IsEnabled)
                {
                    // need to mark buffers as invalid for commit

                    var scratchFile = _env.ScratchBufferPool.GetScratchBufferFile(scratchPage.ScratchFileNumber);

                    var encryptionBuffers = ((IPagerLevelTransactionState)this).CryptoPagerTransactionState[scratchFile.File.Pager];

                    encryptionBuffers[scratchPage.PositionInScratchBuffer].SkipOnTxCommit = true;
                }
            }

            _dirtyPages.Remove(pageNumber);

            UntrackDirtyPage(pageNumber);
        }

        public void FreePage(long pageNumber)
        {
            if (_txState != TxState.None)
                ThrowObjectDisposed();

            try
            {
                UntrackPage(pageNumber);
                Debug.Assert(pageNumber >= 0);

                _pageLocator.Reset(pageNumber); // Remove it from the page locator.

                _freeSpaceHandling.FreePage(this, pageNumber);
                _freedPages.Add(pageNumber);

                DiscardScratchModificationOn(pageNumber);
            }
            catch
            {
                _txState |= TxState.Errored;
                throw;
            }
        }

        public CompactKey AcquireCompactKey()
        {
            // Originally the low level transaction would allow to handle the reuse of compact keys.
            // However, the implementation of reuse has been creating issues when indexing. The ability
            // to reuse keys will be disable until we are able to resolve the underlying cause for it.
            // https://issues.hibernatingrhinos.com/issue/RavenDB-20143
            return new CompactKey(this);
        }

        private class PagerStateCacheItem
        {
            public readonly int FileNumber;
            public readonly PagerState State;

            public PagerStateCacheItem(int file, PagerState state)
            {
                FileNumber = file;
                State = state;
            }
        }

        internal void PrepareForCommit()
        {
            _root.PrepareForCommit();
        }

        public void Commit()
        {
            if (Flags != TransactionFlags.ReadWrite)
                return;// nothing to do

            CommitStage1_CompleteTransaction();

            if (WriteToJournalIsRequired())
            {
                Environment.LastWorkTime = DateTime.UtcNow;
                CommitStage2_WriteToJournal();
            }

            BeforeCommitFinalization?.Invoke(this);
            CommitStage3_DisposeTransactionResources();
        }

        internal Task<bool> AsyncCommit;
        private LowLevelTransaction _asyncCommitNextTransaction;
        private static readonly Task<bool> NoWriteToJournalRequiredTask = Task.FromResult(false);

        /// <summary>
        /// This begins an async commit and starts a new transaction immediately.
        /// The current transaction is considered completed in memory but not yet
        /// committed to disk. This *must* be completed by calling EndAsyncCommit.
        /// </summary>
        public LowLevelTransaction BeginAsyncCommitAndStartNewTransaction(TransactionPersistentContext persistentContext)
        {
            if (Flags != TransactionFlags.ReadWrite)
                ThrowReadTranscationCannotDoAsyncCommit();
            if (_asyncCommitNextTransaction != null)
                ThrowAsyncCommitAlreadyCalled();

            // we have to check the state before we complete the transaction
            // because that would change whether we need to write to the journal
            var writeToJournalIsRequired = WriteToJournalIsRequired();

            CommitStage1_CompleteTransaction();

            var nextTx = new LowLevelTransaction(this, persistentContext,
                writeToJournalIsRequired ? Id + 1 : Id
                );
            _asyncCommitNextTransaction = nextTx;
            AsyncCommit = writeToJournalIsRequired
                  ? Task.Run(() => { CommitStage2_WriteToJournal(); return true; })
                  : NoWriteToJournalRequiredTask;

            var usageIncremented = false;

            try
            {
                _forTestingPurposes?.ActionToCallDuringBeginAsyncCommitAndStartNewTransaction?.Invoke();

                _env.IncrementUsageOnNewTransaction();
                usageIncremented = true;

                _env.ActiveTransactions.Add(nextTx);
                _env.WriteTransactionStarted();

                nextTx.AfterCommitWhenNewTransactionsPrevented += _env.InvokeAfterCommitWhenNewTransactionsPrevented;
                _env.InvokeNewTransactionCreated(nextTx);

                return nextTx;
            }
            catch (Exception)
            {
                // failure here means that we'll try to complete the current transaction normally
                // then throw as if commit was called normally and the next transaction failed

                try
                {
                    if (usageIncremented)
                        _env.DecrementUsageOnTransactionCreationFailure();

                    EndAsyncCommit();
                }
                finally
                {
                    AsyncCommit = null;
                }

                _txState |= TxState.Errored;

                throw;
            }
        }

        private static void ThrowAsyncCommitAlreadyCalled()
        {
            throw new InvalidOperationException("Cannot start a new async commit because one was already started");
        }

        private static void ThrowReadTranscationCannotDoAsyncCommit()
        {
            throw new InvalidOperationException("Only write transactions can do async commit");
        }

        /// <summary>
        /// Completes the async commit began previously. Must be called *within* the 
        /// write lock, and must happen *before* the new transaction call its own commit
        /// method.
        /// </summary>
        public void EndAsyncCommit()
        {
            if (AsyncCommit == null)
            {
                _txState |= TxState.Errored;
                ThrowInvalidAsyncEndWithoutBegin();
                return;// never reached
            }

            try
            {
                AsyncCommit.Wait();
            }
            catch (Exception e)
            {
                // an exception being thrown while / after / somewhere in the middle 
                // of writing to the journal means that we don't know what the current
                // state of the journal is. We have to shut down and run recovery to 
                // come to a known good state
                _txState |= TxState.Errored;
                _env.Options.SetCatastrophicFailure(ExceptionDispatchInfo.Capture(e));

                throw;
            }

            if (AsyncCommit.Result)
                Environment.LastWorkTime = DateTime.UtcNow;

            BeforeCommitFinalization?.Invoke(this);
            CommitStage3_DisposeTransactionResources();
        }

        private static void ThrowInvalidAsyncEndWithoutBegin()
        {
            throw new InvalidOperationException("Cannot call EndAsyncCommit when we don't have an async op running");
        }

        private bool WriteToJournalIsRequired()
        {
            return _dirtyPages.Count > 0
                   || _freedPages.Count > 0
                   // nothing changed in this transaction
                   // allow call to writeToJournal for flushing lazy tx
                   || (IsLazyTransaction == false && _journal?.HasDataInLazyTxBuffer() == true);
        }

        private void CommitStage2_WriteToJournal()
        {
            // In the case of non-lazy transactions, we must flush the data from older lazy transactions
            // to ensure the sequentiality of the data.

            try
            {
                var numberOfWrittenPages = _journal.WriteToJournal(this);
                FlushedToJournal = true;
                _updatePageTranslationTableAndUnusedPages = numberOfWrittenPages.UpdatePageTranslationTableAndUnusedPages;

                if (_forTestingPurposes?.SimulateThrowingOnCommitStage2 == true)
                    _forTestingPurposes.ThrowSimulateErrorOnCommitStage2();

                if (_requestedCommitStats == null) 
                    return;

                _requestedCommitStats.NumberOfModifiedPages = numberOfWrittenPages.NumberOfUncompressedPages;
                _requestedCommitStats.NumberOf4KbsWrittenToDisk = numberOfWrittenPages.NumberOf4Kbs;
            }
            catch
            {
                _txState |= TxState.Errored;
                throw;
            }
        }

        private void CommitStage1_CompleteTransaction()
        {
            if (_txState != TxState.None)
                ThrowObjectDisposed();

            if (Committed)
                ThrowAlreadyCommitted();

            if (RolledBack)
                ThrowAlreadyRolledBack();

            while (_pagesToFreeOnCommit.Count > 0)
            {
                FreePage(_pagesToFreeOnCommit.Pop());
            }

            if (_state.NextPageNumber <= 1)
                ThrowNextPageNumberCannotBeSmallerOrEqualThanOne();

            _txHeader->LastPageNumber = _state.NextPageNumber - 1;
            _state.Root.CopyTo(&_txHeader->Root);

            _txHeader->TxMarker |= TransactionMarker.Commit;

            LastChanceToReadFromWriteTransactionBeforeCommit?.Invoke(this);
        }

        private static void ThrowNextPageNumberCannotBeSmallerOrEqualThanOne([CallerMemberName] string caller = null)
        {
            throw new InvalidOperationException($"{nameof(StorageEnvironmentState.NextPageNumber)} cannot be <= 1 on {caller}.");
        }

        private void CommitStage3_DisposeTransactionResources()
        {
            // an exception being thrown after the transaction has been committed to disk 
            // will corrupt the in memory state, and require us to restart (and recover) to 
            // be in a valid state
            try
            {
                ValidateAllPages();

                Allocator.Release(ref _txHeaderMemory);

                Committed = true;

                _updatePageTranslationTableAndUnusedPages?.ExecuteAfterCommit();

                _env.TransactionAfterCommit(this);

                if (_asyncCommitNextTransaction != null)
                {
                    var old = _asyncCommitNextTransaction.JournalFiles;
                    _asyncCommitNextTransaction.JournalFiles = _env.Journal.Files;
                    foreach (var journalFile in _asyncCommitNextTransaction.JournalFiles)
                    {
                        journalFile.AddRef();
                    }
                    foreach (var journalFile in old)
                    {
                        journalFile.Release();
                    }
                }
            }
            catch (Exception e)
            {
                _txState |= TxState.Errored;
                _env.Options.SetCatastrophicFailure(ExceptionDispatchInfo.Capture(e));
                throw;
            }
        }

        private static void ThrowAlreadyRolledBack()
        {
            throw new InvalidOperationException("Cannot commit rolled-back transaction.");
        }

        private static void ThrowAlreadyCommitted()
        {
            throw new InvalidOperationException("Cannot commit already committed transaction.");
        }


        public void Rollback()
        {
            // here we allow rolling back of errored transaction
            if (_txState.HasFlag(TxState.Disposed))
                ThrowObjectDisposed();


            if (Committed || RolledBack || Flags != (TransactionFlags.ReadWrite))
                return;

            OnRollBack?.Invoke(this);

            ValidateReadOnlyPages();

            foreach (var pageFromScratch in _transactionPages)
            {
                _env.ScratchBufferPool.Free(this, pageFromScratch.ScratchFileNumber, pageFromScratch.PositionInScratchBuffer, null);
            }

            foreach (var pageFromScratch in _unusedScratchPages)
            {
                _env.ScratchBufferPool.Free(this, pageFromScratch.ScratchFileNumber, pageFromScratch.PositionInScratchBuffer, null);
            }

            // release scratch file page allocated for the transaction header
            Allocator.Release(ref _txHeaderMemory);

            using (_env.PreventNewTransactions())
            {
                _env.ScratchBufferPool.UpdateCacheForPagerStatesOfAllScratches();
                _env.Journal.UpdateCacheForJournalSnapshots();
            }

            RolledBack = true;
        }

        public void RetrieveCommitStats(out CommitStats stats)
        {
            _requestedCommitStats = stats = new CommitStats();
        }

        public string GetTxState()
        {
            return _txState.ToString();
        }

        private PagerState _lastState;
        private bool _isLazyTransaction;

        internal bool FlushInProgressLockTaken;
        private ByteString _txHeaderMemory;
        internal ImmutableAppendOnlyList<JournalFile> JournalFiles;
        internal bool AlreadyAllowedDisposeWithLazyTransactionRunning;
        public DateTime TxStartTime;
        public bool IsCloned;
        internal long? LocalPossibleOldestReadTransaction;
        internal RacyConcurrentBag.Node ActiveTransactionNode;
        public Transaction Transaction;

        public void EnsurePagerStateReference(ref PagerState state)
        {
            if (state == _lastState || state == null)
                return;

            _forTestingPurposes?.ActionToCallDuringEnsurePagerStateReference?.Invoke();

            if (_pagerStates.Contains(state))
            {
                _lastState = state;
                return;
            }

            state = state.CurrentPager.GetPagerStateAndAddRefAtomically(); // state might hold released pagerState, and we want to add ref to the current (i.e. data file was re-allocated and a new state is now available). RavenDB-6950

            _lastState = state;

            if (_pagerStates.Add(state) == false)
            {
                // the state is already on the list but we already added a reference to it so now we need to release it

                state.Release();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void OnAfterCommitWhenNewTransactionsPrevented()
        {
            // the event cannot be called outside this class while we need to call it in 
            // StorageEnvironment.TransactionAfterCommit
            AfterCommitWhenNewTransactionsPrevented?.Invoke(this);
        }


#if VALIDATE_PAGES

        private Dictionary<long, ulong> readOnlyPages = new Dictionary<long, ulong>();
        private Dictionary<long, ulong> writablePages = new Dictionary<long, ulong>();
        private readonly HashSet<long> dirtyPagesValidate = new HashSet<long>();

        private void ValidateAllPages()
        {
            ValidateWritablePages();
            ValidateReadOnlyPages();
        }

        private void ValidateReadOnlyPages()
        {
            foreach (var readOnlyKey in readOnlyPages)
            {
                long pageNumber = readOnlyKey.Key;
                if (dirtyPagesValidate.Contains(pageNumber))
                    VoronUnrecoverableErrorException.Raise(_env, "Read only page is dirty (which means you are modifying a page directly in the data -- non transactionally -- ).");

                var page = GetPage(pageNumber);
                
                ulong pageHash = StorageEnvironment.CalculatePageChecksum(page.Pointer, page.PageNumber, page.Flags, page.OverflowSize);
                if (pageHash != readOnlyKey.Value)
                    VoronUnrecoverableErrorException.Raise(_env, "Read only page content is different (which means you are modifying a page directly in the data -- non transactionally -- ).");
            }
        }

        private void ValidateWritablePages()
        {
            foreach (var writableKey in writablePages)
            {
                long pageNumber = writableKey.Key;
                if (!dirtyPagesValidate.Contains(pageNumber))
                    VoronUnrecoverableErrorException.Raise(_env, "Writable key is not dirty (which means you are asking for a page modification for no reason).");
            }
        }

        private void UntrackPage(long pageNumber)
        {
            readOnlyPages.Remove(pageNumber);
            writablePages.Remove(pageNumber);
        }

        private void TrackWritablePage(Page page)
        {
            if (readOnlyPages.ContainsKey(page.PageNumber))
                readOnlyPages.Remove(page.PageNumber);

            if (writablePages.ContainsKey(page.PageNumber) == false)
            {
                ulong pageHash = StorageEnvironment.CalculatePageChecksum(page.Pointer, page.PageNumber, page.Flags, page.OverflowSize);
                writablePages[page.PageNumber] = pageHash;
            }
        }

        private void TrackReadOnlyPage(Page page)
        {
            if (writablePages.ContainsKey(page.PageNumber))
                return;

            ulong pageHash = StorageEnvironment.CalculatePageChecksum(page.Pointer, page.PageNumber, page.Flags, page.OverflowSize);

            ulong storedHash;
            if (readOnlyPages.TryGetValue(page.PageNumber, out storedHash))
            {
                if (pageHash != storedHash)
                    VoronUnrecoverableErrorException.Raise(_env, "Read Only Page has change between tracking requests. Page #" + page.PageNumber);
            }
            else
            {
                readOnlyPages[page.PageNumber] = pageHash;
            }
        }

        private void TrackDirtyPage(long page)
        {
            dirtyPagesValidate.Add(page);
        }

        private void UntrackDirtyPage(long page)
        {
            dirtyPagesValidate.Remove(page);
        }

#else
        // This will only be used as placeholder for compilation when not running with validation started.

        [Conditional("VALIDATE_PAGES")]
        private void ValidateAllPages() { }

        [Conditional("VALIDATE_PAGES")]
        private void ValidateReadOnlyPages() { }

        [Conditional("VALIDATE_PAGES")]
        private void TrackWritablePage(Page page) { }

        [Conditional("VALIDATE_PAGES")]
        private void TrackReadOnlyPage(Page page) { }

        [Conditional("VALIDATE_PAGES")]
        private void UntrackPage(long pageNumber) { }

        [Conditional("VALIDATE_PAGES")]
        private void TrackDirtyPage(long page) { }

        [Conditional("VALIDATE_PAGES")]
        private void UntrackDirtyPage(long page) { }
#endif

        internal TransactionHeader* GetTransactionHeader()
        {
            return _txHeader;
        }

        internal TestingStuff ForTestingPurposesOnly()
        {
            if (_forTestingPurposes != null)
                return _forTestingPurposes;

            return _forTestingPurposes = new TestingStuff(this);
        }

        internal class TestingStuff
        {
            private readonly LowLevelTransaction _tx;
            internal bool SimulateThrowingOnCommitStage2 = false;

            internal Action ActionToCallDuringEnsurePagerStateReference;
            internal Action ActionToCallJustBeforeWritingToJournal;
            internal Action ActionToCallDuringBeginAsyncCommitAndStartNewTransaction;

            public TestingStuff(LowLevelTransaction tx)
            {
                _tx = tx;
            }

            internal void ThrowSimulateErrorOnCommitStage2()
            {
                throw new InvalidOperationException("Simulation error");
            }

            internal IDisposable CallDuringEnsurePagerStateReference(Action action)
            {
                ActionToCallDuringEnsurePagerStateReference = action;

                return new DisposableAction(() => ActionToCallDuringEnsurePagerStateReference = null);
            }

            internal IDisposable CallJustBeforeWritingToJournal(Action action)
            {
                ActionToCallJustBeforeWritingToJournal = action;

                return new DisposableAction(() => ActionToCallJustBeforeWritingToJournal = null);
            }

            internal IDisposable CallDuringBeginAsyncCommitAndStartNewTransaction(Action action)
            {
                ActionToCallDuringBeginAsyncCommitAndStartNewTransaction = action;

                return new DisposableAction(() => ActionToCallDuringBeginAsyncCommitAndStartNewTransaction = null);
            }

            internal HashSet<PagerState> GetPagerStates()
            {
                return _tx._pagerStates;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal PersistentDictionary GetEncodingDictionary(long dictionaryId)
        {
            var dictionaryLocator = _env.DictionaryLocator;
            if (dictionaryLocator.TryGet(dictionaryId, out var dictionary))
                return dictionary;

            return _env.CreateEncodingDictionary(GetPage(dictionaryId));
        }
    }
}
