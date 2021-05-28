﻿using System;
using Blockcore.AsyncWork;
using Blockcore.Base;
using Blockcore.Connection;
using Blockcore.Consensus.BlockInfo;
using Blockcore.Consensus.Chain;
using Blockcore.Interfaces;
using Blockcore.Networks;
using Blockcore.Signals;
using Blockcore.Utilities;
using Microsoft.Extensions.Logging;
using NBitcoin;
using StatsN;

namespace Blockcore.Features.BlockStore
{
    /// <summary>
    /// The goal of this behavior is to ensure that we have always a Proven Header for each block signaled, because our node
    /// must be able to serve a Proven Header for every block we announce
    /// </summary>
    /// <seealso cref="BlockStoreSignaled" />
    public class ProvenHeadersBlockStoreSignaled : BlockStoreSignaled
    {
        private readonly Network network;
        private readonly IProvenBlockHeaderStore provenBlockHeaderStore;

        public ProvenHeadersBlockStoreSignaled(
            Network network,
            IBlockStoreQueue blockStoreQueue,
            StoreSettings storeSettings,
            IChainState chainState,
            IConnectionManager connection,
            INodeLifetime nodeLifetime,
            ILoggerFactory loggerFactory,
            IInitialBlockDownloadState initialBlockDownloadState,
            IProvenBlockHeaderStore provenBlockHeaderStore,
            ISignals signals,
            IAsyncProvider asyncProvider)
            : base(blockStoreQueue, storeSettings, chainState, connection, nodeLifetime, loggerFactory, initialBlockDownloadState, signals, asyncProvider)
        {
            this.network = Guard.NotNull(network, nameof(network));
            this.provenBlockHeaderStore = Guard.NotNull(provenBlockHeaderStore, nameof(provenBlockHeaderStore));
        }

        /// <inheritdoc />
        /// <remarks>When a block is signaled, we check if its header is a Proven Header, if not, we need to generate and store it.</remarks>
        protected override void AddBlockToQueue(ChainedHeaderBlock blockPair, bool isIBD)
        {
            int blockHeight = blockPair.ChainedHeader.Height;

            if (blockPair.ChainedHeader.ProvenBlockHeader != null)
            {
                this.logger.LogDebug("Current header is already a Proven Header.");

                // Add to the store, to be sure we actually store it anyway.
                // It's ProvenBlockHeaderStore responsibility to prevent us to store it twice.
                this.provenBlockHeaderStore.AddToPendingBatch(blockPair.ChainedHeader.ProvenBlockHeader, new HashHeightPair(blockPair.ChainedHeader.HashBlock, blockHeight));
            }
            else
            {
                // Ensure we doesn't have already the ProvenHeader in the store.
                ProvenBlockHeader provenHeader = this.provenBlockHeaderStore.GetAsync(blockPair.ChainedHeader.Height).GetAwaiter().GetResult();

                // Proven Header not found? create it now.
                if (provenHeader == null)
                {
                    this.logger.LogDebug("Proven Header at height {0} NOT found.", blockHeight);

                    this.CreateAndStoreProvenHeader(blockHeight, blockPair, isIBD);
                }
                else
                {
                    uint256 signaledHeaderHash = blockPair.Block.Header.GetHash();

                    // If the Proven Header is the right one, then it's OK and we can return without doing anything.
                    uint256 provenHeaderHash = provenHeader.GetHash();
                    if (provenHeaderHash == signaledHeaderHash)
                    {
                        this.logger.LogDebug("Proven Header {0} found.", signaledHeaderHash);
                    }
                    else
                    {
                        this.logger.LogDebug("Found a proven header with a different hash, recreating PH. Expected Hash: {0}, found Hash: {1}.", signaledHeaderHash, provenHeaderHash);

                        // A reorg happened so we recreate a new Proven Header to replace the wrong one.
                        this.CreateAndStoreProvenHeader(blockHeight, blockPair, isIBD);
                    }
                }
            }

            // At the end, if no exception happened, control is passed back to base AddBlockToQueue.
            base.AddBlockToQueue(blockPair, isIBD);

            StatsBlocks(blockHeight, blockPair);

        }

        private void StatsBlocks(long blockHeight, ChainedHeaderBlock blockPair)
        {
            Statsd statsd = Statsd.New(new StatsdOptions() { HostOrIp = "127.0.0.1", Port = 8125 });

            statsd.GaugeAsync("BlockSize", blockPair.Block.BlockSize.Value);
            statsd.GaugeAsync("TXinBlock", blockPair.Block.Transactions.Count);
            statsd.GaugeAsync("BlockChainWork", Convert.ToInt64(blockPair.ChainedHeader.ChainWork.GetLow32()));
            statsd.CountAsync("getBlock.count");
            statsd.GaugeAsync("block.height", blockHeight);


        }

        /// <summary>
        /// Creates and store a <see cref="ProvenBlockHeader" /> generated by the signaled <see cref="ChainedHeaderBlock"/>.
        /// </summary>
        /// <param name="blockHeight">Height of the block used to generate its Proven Header.</param>
        /// <param name="chainedHeaderBlock">Block used to generate its Proven Header.</param>
        /// <param name="isIBD">Is node in IBD.</param>
        private void CreateAndStoreProvenHeader(int blockHeight, ChainedHeaderBlock chainedHeaderBlock, bool isIBD)
        {
            PosBlock block = (PosBlock)chainedHeaderBlock.Block;

            ProvenBlockHeader newProvenHeader = ((PosConsensusFactory)this.network.Consensus.ConsensusFactory).CreateProvenBlockHeader(block);

            uint256 provenHeaderHash = newProvenHeader.GetHash();
            this.provenBlockHeaderStore.AddToPendingBatch(newProvenHeader, new HashHeightPair(provenHeaderHash, blockHeight));

            this.logger.LogDebug("Created Proven Header at height {0} with hash {1} and adding to the pending batch to be stored.", blockHeight, provenHeaderHash);

            // If our node is in IBD the block will not be announced to peers.
            // If not in IBD the signaler may expect the block header to be of type PH.
            // TODO: Memory foot print:
            // This design will cause memory to grow over time (depending on how long the node is running)
            // based on the size of the Proven Headers (a proven header can be up to 1000 bytes).
            // This is also correct for regular header (which are 80 bytes in size).
            // If we want to be able to control the size of PH we will need to change the logic
            // in ProvenHeadersBlockStoreBehavior and load the PH from the PH store instead
            if (!isIBD)
                chainedHeaderBlock.SetHeader(newProvenHeader);
        }
    }
}