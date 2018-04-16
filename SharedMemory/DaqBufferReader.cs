// SharedMemory  (File: SharedMemory\DAQBufferReader.cs)
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
    public unsafe class DAQBufferReader : DAQBuffer
    {

        /// <summary>
        /// the next value of the continuous counter
        /// </summary>
        private long _node_readcounter = -1;
        /// <summary>
        /// The read pointer in Nodeheader for the next read, -1 to set the actual (last) value
        /// </summary>
        private int _node_readpointer = -1;

         /// <summary>
        /// Hold diagnostic data
        /// </summary>
        public struct ReadDiagnostics
        {
            /// <summary>
            /// needed Ticks to wait for data up to signaling from write process
            /// </summary>
            public long waitticks;
            /// <summary>
            /// copy of the  Node for debugging
            /// </summary>
            public Node ReadNode;
            public override string ToString()
            {
                return string.Format("{0} wait {1} ms", ReadNode.ToString(), 1.0* waitticks / TimeSpan.TicksPerMillisecond);
            }
        }
        public ReadDiagnostics DiagInfo;

        #region Constructors


        /// <summary>
        /// Opens an existing <see cref="CircularBuffer"/> with the specified name.
        /// </summary>
        /// <param name="name">The name of an existing <see cref="CircularBuffer"/> previously created with <see cref="SharedBuffer.IsOwnerOfSharedMemory"/>=true</param>
        public DAQBufferReader(string name)
            : base(name, 0, 0, false)
        {
            Open();
        }


        #endregion

   
        #region Node Reading

        /// <summary>
        /// Returns a copy of the shared memory header
        /// </summary>
        public NodeHeader ReadNodeHeader()
        {
            return (NodeHeader)Marshal.PtrToStructure(new IntPtr(_nodeHeader), typeof(NodeHeader));
        }

       

        /// <summary>
        /// Attempts to reserve a node from the linked-list for reading with the specified timeout
        /// </summary>
        /// <param name="timeout">The number of milliseconds to wait if a node is not immediately available for reading.</param>
        /// <returns>An unsafe pointer to the node if successful, otherwise null</returns>
        protected virtual Node* GetNodeForReading(int timeout)
        {
            if (_node_readpointer == -1)
                Interlocked.Exchange(ref _node_readpointer, _nodeHeader->WriteLastIndex); //first read, init _readstart
            
            //need to wait?
            long lastcounter=0;
            Interlocked.Exchange(ref lastcounter, _nodeHeader->WriteLastCounter);
            if (_node_readcounter == -1)
                _node_readcounter = lastcounter;//first read, init counter
            if (_node_readcounter > lastcounter)
            { // No data is available, wait for it
                if (timeout <= 0) // check value and set default 10 s
                    timeout = 10000;                
                DataExists.Reset();
                var starttick = Stopwatch.GetTimestamp();
                if (!DataExists.WaitOne(timeout))
                    return null;
                DiagInfo.waitticks = Stopwatch.GetTimestamp() - starttick;
            }
            int blockIndex = _node_readpointer;
            Node* node = this[blockIndex];
            node->ReserveNodeRead();
            //set data for next node
            if (Interlocked.CompareExchange(ref _node_readpointer, node->Next, blockIndex) == blockIndex)
                return node;
            throw new Exception("Another thread has already acquired this node for reading!");
        }
        protected virtual void FreeNode(Node* node)
        {
            DiagInfo.ReadNode = *node;
            node->FreeNodeRead();

        }

        /// <summary>
        /// Reads the next available node for reading into the specified byte array
        /// </summary>
        /// <param name="destination">Reference to the buffer</param>
        /// <param name="DontThrowException"> </param>
        /// <param name="timeout">The maximum number of milliseconds to wait for a node to become available for reading (default 10000ms)</param>
        /// <returns>positive: The number of bytes read, 0: read timeout occured, -1: No data continuity or Buffer overflow</returns>
        /// <remarks>The maximum number of bytes that can be read is the minimum of the length of <paramref name="destination"/> subtracted by <paramref name="startIndex"/> and <see cref="NodeBufferSize"/>.</remarks>
        public virtual int Read(byte[] destination,  Boolean DontThrowException = false, int timeout = 10000)
        {
            Node* node = GetNodeForReading(timeout);
            if (node == null)
            {
                throw new Exception("Read Timeout");
                return 0; //timeout
            }

            int result = -1; //no data continuity
            Debug.Print("node->Index {1}, node->Counter {2}, _nextreadcounter {3}", 0, node->Index, node->ContinueCounter, _node_readcounter);
            if (node->ContinueCounter == _node_readcounter)
            {
                int amount = Math.Min(destination.Length, node->AmountWritten);
                result = amount;
                // Copy the data
                Marshal.Copy(new IntPtr(BufferStartPtr + node->Offset), destination, 0, amount);
                FreeNode(node);
                _node_readcounter++;
            }
            else
            {
                FreeNode(node);
                throw new Exception("No data continuity. Buffer overflow. Read faster from DAQBuffer");
            }
            return result;
        }


        #endregion
    }
}
