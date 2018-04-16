// SharedMemory (File: SharedMemory\DAQBuffer.cs)
// Copyright (c) 2014 Justin Stenning
// http://spazzarama.com
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
// The SharedMemory library is inspired by the following Code Project article:
//   "Fast IPC Communication Using Shared Memory and InterlockedCompareExchange"
//   http://www.codeproject.com/Articles/14740/Fast-IPC-Communication-Using-Shared-Memory-and-Int

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Permissions;
using System.Text;
using System.Threading;

namespace SharedMemory
{
    /// <summary>
    /// A lock-free FIFO shared memory circular buffer (or ring buffer) utilising a <see cref="MemoryMappedFile"/>.
    /// </summary>
    [PermissionSet(SecurityAction.LinkDemand)]
    [PermissionSet(SecurityAction.InheritanceDemand)]
    public unsafe class DAQBuffer : SharedBuffer
    {
        #region Public/Protected properties
        
        /// <summary>
        /// The number of nodes within the circular linked-list
        /// </summary>
        public int NodeCount { get; private set; }
        
        /// <summary>
        /// The buffer size of each node
        /// </summary>
        public int NodeBufferSize { get; private set; }
        
        /// <summary>
        /// Event signaled when data has been written if the reading index has caught up to the writing index
        /// </summary>
        protected EventWaitHandle DataExists { get; set; }

        /// <summary>
        /// Event signaled when a node becomes available after reading if the writing index has caught up to the reading index
        /// </summary>
        //protected EventWaitHandle NodeAvailable { get; set; }

        /// <summary>
        /// The offset relative to <see cref="SharedBuffer.BufferStartPtr"/> where the node header starts within the buffer region of the shared memory
        /// </summary>
        protected virtual long NodeHeaderOffset
        {
            get
            {
                return 0;
            }
        }
            
        /// <summary>
        /// Where the linked-list nodes are located within the buffer
        /// </summary>
        protected virtual long NodeOffset
        {
            get
            {
                return NodeHeaderOffset + Marshal.SizeOf(typeof(NodeHeader));
            }
        }

        /// <summary>
        /// Where the list of buffers are located within the shared memory
        /// </summary>
        protected virtual long NodeBufferOffset
        {
            get
            {
                return NodeOffset + (Marshal.SizeOf(typeof(Node)) * NodeCount);
            }
        }

        /// <summary>
        /// Provide direct access to the Node[] memory
        /// </summary>
        /// <param name="i"></param>
        /// <returns></returns>
        protected virtual Node* this[int i]
        {
            get
            {
                if (i < 0 || i >= NodeCount)
                    throw new ArgumentOutOfRangeException();

                return ((Node*)(BufferStartPtr + NodeOffset)) + i;
            }
        }

        #endregion

        #region Private field members

        internal NodeHeader* _nodeHeader = null;
        
        #endregion

        #region Structures

        /// <summary>
        /// Provides cursors for the circular buffer along with dimensions
        /// </summary>
        /// <remarks>This structure is the same size on 32-bit and 64-bit architectures.</remarks>
        [StructLayout(LayoutKind.Sequential)]
        public struct NodeHeader
        {

            /// <summary>
            /// The index of the last written node
            /// </summary>
            public volatile int WriteLastIndex;

            /// <summary>
            /// The continues counter of the last written node
            /// </summary>
            public  long WriteLastCounter;


            /// <summary>
            /// The index of the first unwritable node
            /// </summary>
            public volatile int WriteEnd;
            /// <summary>
            /// The index of the next writable node
            /// </summary>
            public volatile int WriteStart;

            /// <summary>
            /// The number of nodes within the buffer
            /// </summary>
            public int NodeCount;

            /// <summary>
            /// The size of the buffer for each node
            /// </summary>
            public int NodeBufferSize;
        }

        /// <summary>
        /// Represents a node within the buffer's circular linked list
        /// </summary>
        /// <remarks>This structure is the same size on 32-bit and 64-bit architectures.</remarks>
        [StructLayout(LayoutKind.Sequential)]
        public struct Node
        {
            /// <summary>
            /// The previous node.
            /// </summary>
            public int Next;

            /// <summary>
            /// The next node.
            /// </summary>
            public int Prev;

         

             /// <summary>
            /// Represents the offset relative to <see cref="SharedBuffer.BufferStartPtr"/> where the data for this node can be found.
            /// </summary>
            public long Offset;
            
            /// <summary>
            /// Represents the index of the current node.
            /// </summary>
            public int Index;

            /// <summary>
            /// Represents the continues counter of the current node to detect data discontinuity
            /// </summary>
            public long ContinueCounter;

            /// <summary>
            /// Holds the number of bytes written into this node.
            /// </summary>
            public int AmountWritten;

            /// <summary>
            /// A indicated node is used reading
            /// </summary>
            private int UsingCounterNodeRead;
            /// <summary>
            /// A indicated node is used reading
            /// </summary>
            private int UsingNodeWrite;
            /// <summary>
            /// lock variable to set UsingNodeRead, UsingNodeWrite
            /// </summary>
            private int locker;

            /// <summary>
            /// For debugging: the needed ticks for the reserve operations
            /// </summary>
            public int WaitTicksForReserve;

            /// <summary>
            /// Wait and set lock 
            /// </summary>
            private void GetLock()
            {
                for (;;)
                    if (0 == Interlocked.CompareExchange(ref locker, 1, 0))
                        return;
            }

            /// <summary>
            /// Free the lock
            /// </summary>
            private void FreeLock()
            {
                //Release the lock
                Interlocked.Exchange(ref locker, 0);
            }
            /// <summary>
            /// Reserve a node for Writing
            /// Node can written if it is not read (UsingCounterNodeRead==0) 
            /// </summary>
            public void ReserveNodeWrite()
            {
                long starttic = System.Diagnostics.Stopwatch.GetTimestamp();
                for (;;)
                {
                    GetLock();
                    if ((UsingNodeWrite == 0) && (UsingCounterNodeRead == 0))
                    {
                        UsingNodeWrite=1;
                        break;
                    }
                    FreeLock();
                }
                FreeLock();
                WaitTicksForReserve = (int)(Stopwatch.GetTimestamp() - starttic);
                return;
            }
            /// <summary>
            /// Increment UsingNodeRead node for Read, so no write is possible, but other process can read
            /// Wait, if node is actual written
            /// </summary>
            public void ReserveNodeRead()
            {
                long starttic = System.Diagnostics.Stopwatch.GetTimestamp();
                for (;;)
                {
                    GetLock();
                    if (UsingNodeWrite==0)
                    {
                        UsingCounterNodeRead++;
                        break;
                    }
                    FreeLock();
                }
                FreeLock();
                WaitTicksForReserve = (int)(Stopwatch.GetTimestamp() - starttic);
                return;
            }
            /// <summary>
            /// Decrement the UsingCounterNodeRead
            /// Caution: Don't forget call FreeNodeRead() after ReserveNodeRead()!
            /// </summary>
            public void FreeNodeRead()
            {
                if (Interlocked.Decrement(ref UsingCounterNodeRead) < 0)
                    throw new Exception("UsingNodeRead<0"); //should not happen
            }

            /// <summary>
            /// Free the usingNode Write lock
            /// Caution: Don't forget call FreeNodeWrite() after ReserveNodeWrite()!
            /// </summary>
            public void FreeNodeWrite()
            {
                Interlocked.Exchange(ref UsingNodeWrite , 0);
            }
            public override string ToString()
            {
                string format = "Node Counter={0}, Index={4}, Next={1}, Prev={2}, Amount={5} WaitTicksForReserve={6:0}, Offset={3}";
                return string.Format(format, ContinueCounter, Next, Prev, Offset, Index, AmountWritten, WaitTicksForReserve);
            }
            
        }

        #endregion

        #region Constructors

        internal DAQBuffer(string name, int nodeCount, int nodeBufferSize, bool ownsSharedMemory)
            : base(name, nodeCount==0 ? 0 : Marshal.SizeOf(typeof(NodeHeader)) + (Marshal.SizeOf(typeof(Node)) * nodeCount) + (nodeCount * (long)nodeBufferSize), ownsSharedMemory)
        {
            #region Argument validation
            if (ownsSharedMemory && nodeCount < 2)
                throw new ArgumentOutOfRangeException("nodeCount", nodeCount, "The node count must be a minimum of 2.");
#if DEBUG
            else if (!ownsSharedMemory && (nodeCount != 0 || nodeBufferSize > 0))
                System.Diagnostics.Debug.Write("Node count and nodeBufferSize are ignored when opening an existing shared memory circular buffer.", "Warning");
#endif
            #endregion

            if (IsOwnerOfSharedMemory)
            {
                NodeCount = nodeCount;
                NodeBufferSize = nodeBufferSize;
            }
        }

        #endregion

        #region Open / Close

        /// <summary>
        /// Attempts to create the <see cref="EventWaitHandle"/> handles and initialise the node header and buffers.
        /// </summary>
        /// <returns>True if the events and nodes were initialised successfully.</returns>
        protected override bool DoOpen()
        {
            // Create signal events
            var eventname = Name + "_evt_dataexists";
            //need ManualReset: When signaled, the EventWaitHandle releases all waiting threads and remains signaled until it is manually reset.
            //AutoReset: the EventWaitHandle resets automatically after releasing a single thread.  
            DataExists = new EventWaitHandle(false, EventResetMode.ManualReset, eventname);

            if (IsOwnerOfSharedMemory)
            {
                // Retrieve pointer to node header
                _nodeHeader = (NodeHeader*)(BufferStartPtr + NodeHeaderOffset);

                // Initialise the node header
                InitialiseNodeHeader();

                // Initialise nodes entries
                InitialiseLinkedListNodes();
            }
            else
            {
                // Load the NodeHeader
                _nodeHeader = (NodeHeader*)(BufferStartPtr + NodeHeaderOffset);
                NodeCount = _nodeHeader->NodeCount;
                NodeBufferSize = _nodeHeader->NodeBufferSize;
            }

            return true;
        }

        /// <summary>
        /// Initialises the node header within the shared memory. Only applicable if <see cref="SharedBuffer.IsOwnerOfSharedMemory"/> is true.
        /// </summary>
        private void InitialiseNodeHeader()
        {
            if (!IsOwnerOfSharedMemory)
                return;

            NodeHeader header = new NodeHeader();
            //header.ReadStart = 0;
            //header.ReadEnd = 0;
            header.WriteEnd = 0;
            header.WriteStart = 0;
            header.NodeBufferSize = NodeBufferSize;
            header.NodeCount = NodeCount;
            base.Write<NodeHeader>(ref header, NodeHeaderOffset);
        }

        /// <summary>
        /// Initialise the nodes of the circular linked-list. Only applicable if <see cref="SharedBuffer.IsOwnerOfSharedMemory"/> is true.
        /// </summary>
        private void InitialiseLinkedListNodes()
        {
            if (!IsOwnerOfSharedMemory)
                return;

            int N = 0;

            Node[] nodes = new Node[NodeCount];

            // First node
            nodes[N].Next = 1;
            nodes[N].Prev = NodeCount - 1;
            nodes[N].Offset = NodeBufferOffset;
            nodes[N].Index = N;
            // Middle nodes
            for (N = 1; N < NodeCount - 1; N++)
            {
                nodes[N].Next = N + 1;
                nodes[N].Prev = N - 1;
                nodes[N].Offset = NodeBufferOffset + (NodeBufferSize * N);
                nodes[N].Index = N;
            }
            // Last node
            nodes[N].Next = 0;
            nodes[N].Prev = NodeCount - 2;
            nodes[N].Offset = NodeBufferOffset + (NodeBufferSize * N);
            nodes[N].Index = N;

            // Write the nodes to the shared memory
            base.WriteArray<Node>(nodes, 0, nodes.Length, NodeOffset);
        }

        /// <summary>
        /// Closes the events. The shared memory could still be open within one or more other instances.
        /// </summary>
        protected override void DoClose()
        {
            if (DataExists != null)
            {
                (DataExists as IDisposable).Dispose();
                DataExists = null;
                //(NodeAvailable as IDisposable).Dispose();
                //NodeAvailable = null;
            }

            _nodeHeader = null;
        }

        #endregion

       }
}
