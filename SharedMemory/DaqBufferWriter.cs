// SharedMemory (File: SharedMemory\DAQBufferWriter.cs)
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
    public unsafe class DAQBufferWriter : DAQBuffer
    {
        #region  field members , properties

        /// <summary>
        /// the next value of the continuous counter
        /// </summary>
        private long _writecounter = 0;
        
        /// <summary>
        /// Get the continues write counter
        /// </summary>
        public long WriteCounter { get { return _writecounter; } }

        /// <summary>
        /// Hold diagnostic data
        /// </summary>
        public struct WriteDiagnostics
        {
            /// <summary>
            /// copy of the posted Node for debugging
            /// </summary>
            public Node PostedNode;
            public override string ToString()
            {
                //                return string.Format("{0} countReserve={1}", PostedNode.ToString(), countReserveNode);
                return PostedNode.ToString();
            }
        }


        public WriteDiagnostics DiagInfo;
 
        #endregion

        #region Constructors

        /// <summary>
        /// Creates and opens a new <see cref="CircularBuffer"/> instance with the specified name, node count and buffer size per node.
        /// </summary>
        /// <param name="name">The name of the shared memory to be created</param>
        /// <param name="nodeCount">The number of nodes within the circular linked-list (minimum of 2)</param>
        /// <param name="nodeBufferSize">The buffer size per node in bytes. The total shared memory size will be <code>Marshal.SizeOf(SharedMemory.SharedHeader) + Marshal.SizeOf(CircularBuffer.NodeHeader) + (Marshal.SizeOf(CircularBuffer.Node) * nodeCount) + (bufferSize * nodeCount)</code></param>
        /// <remarks>
        /// <para>The maximum total shared memory size is dependent upon the system and current memory fragmentation.</para>
        /// <para>The shared memory layout on 32-bit and 64-bit architectures is:<br />
        /// <code>
        /// |       Header       |   NodeHeader  | Node[0] | ... | Node[N-1] | buffer[0] | ... | buffer[N-1] |<br />
        /// |      16-bytes      |    24-bytes   |       32-bytes * N        |     NodeBufferSize * N        |<br />
        ///                      |------------------------------BufferSize-----------------------------------|<br />
        /// |-----------------------------------------SharedMemorySize---------------------------------------|
        /// </code>
        /// </para>
        /// </remarks>
        public DAQBufferWriter(string name, int nodeCount, int nodeBufferSize)
            : base(name, nodeCount, nodeBufferSize, true)
        {
            Open();
        }
        #endregion
     

        #region Node Writing

        /// <summary>
        /// Reserve a node from the linked-list for writing
        /// </summary>
        /// <returns>An unsafe pointer to the node if successful, otherwise null</returns>
        protected virtual Node* GetNodeForWriting()
        {
            int blockIndex = _nodeHeader->WriteStart;
            Node* node = this[blockIndex];
            node->ReserveNodeWrite();
            node->ContinueCounter = _writecounter++;
            Interlocked.Exchange(ref _nodeHeader->WriteStart, node->Next);
            return node;
        }

        /// <summary>
        /// Makes a node available for reading after writing is complete
        /// </summary>
        /// <param name="node">An unsafe pointer to the node to return</param>
        protected virtual void PostNode(Node* node)
        {
            //set pointer oft last written Node
            Interlocked.Exchange(ref _nodeHeader->WriteLastIndex, node->Index);
            //set pointer oft last written Node
            Interlocked.Exchange(ref _nodeHeader->WriteLastCounter, node->ContinueCounter);
            // Move the pointer one forward
            Interlocked.Exchange(ref _nodeHeader->WriteEnd , node->Next);
            DiagInfo.PostedNode = *node;
            // free node
            node->FreeNodeWrite();
            // Signal the "data exists" event for read threads
            DataExists.Set();
        }
        
        /// <summary>
        /// Writes the byte array buffer to the next available node for writing
        /// </summary>
        /// <param name="source">Reference to the buffer to write</param>
        /// <param name="startIndex">The index within the buffer to start writing from</param>
        /// <returns>The number of bytes written</returns>
        /// <remarks>The maximum number of bytes that can be written is the minimum of the length of <paramref name="source"/> and <see cref="NodeBufferSize"/>.</remarks>
        public virtual int Write(byte[] source, int startIndex = 0)
        {
            // Grab a node for writing
            Node* node = GetNodeForWriting();

            // Copy the data
            int amount = Math.Min(source.Length - startIndex, NodeBufferSize);
            
            Marshal.Copy(source, startIndex, new IntPtr(BufferStartPtr + node->Offset), amount);
            node->AmountWritten = amount;
            

            // Writing is complete, make readable
            PostNode(node);
            return amount;
        }

        /// <summary>
        /// Writes the structure array buffer to the next available node for writing
        /// </summary>
        /// <param name="source">Reference to the buffer to write</param>
        /// <param name="startIndex">The index within the buffer to start writing from</param>
        /// <param name="timeout">The maximum number of milliseconds to wait for a node to become available for writing (default 1000ms)</param>
        /// <returns>The number of elements written</returns>
        /// <remarks>The maximum number of elements that can be written is the minimum of the length of <paramref name="source"/> subtracted by <paramref name="startIndex"/> and <see cref="NodeBufferSize"/> divided by <code>FastStructure.SizeOf&gt;T&lt;()</code>.</remarks>        
        public virtual int Write<T>(T[] source, int startIndex = 0)
            where T : struct
        {
            // Grab a node for writing
            Node* node = GetNodeForWriting();
            if (node == null) return 0;

            // Write the data using the FastStructure class (much faster than the MemoryMappedViewAccessor WriteArray<T> method)
            int count = Math.Min(source.Length - startIndex, NodeBufferSize / FastStructure.SizeOf<T>());
            base.WriteArray<T>(source, startIndex, count, node->Offset);
            node->AmountWritten = count * FastStructure.SizeOf<T>();

            // Writing is complete, make node readable
            PostNode(node);

            return count;
        }

        /// <summary>
        /// Writes the structure to the next available node for writing
        /// </summary>
        /// <typeparam name="T">The structure type to be written</typeparam>
        /// <param name="source">The structure to be written</param>
        /// <param name="timeout">The maximum number of milliseconds to wait for a node to become available for writing (default 1000ms)</param>
        /// <returns>The number of bytes written - larger than 0 if successful</returns>
        /// <exception cref="ArgumentOutOfRangeException">If the size of the <typeparamref name="T"/> structure is larger than <see cref="NodeBufferSize"/>.</exception>
        public virtual int Write<T>(ref T source)
            where T : struct
        {
            int structSize = Marshal.SizeOf(typeof(T));
            if (structSize > NodeBufferSize)
                throw new ArgumentOutOfRangeException("T", "The size of structure " + typeof(T).Name + " is larger than NodeBufferSize");

            // Attempt to retrieve a node for writing
            Node* node = GetNodeForWriting();
            if (node == null) return 0;

            // Copy the data using the MemoryMappedViewAccessor
            base.Write<T>(ref source, node->Offset);
            node->AmountWritten = structSize;

            // Return the node for further writing
            PostNode(node);

            return structSize;
        }

        /// <summary>
        /// Writes <paramref name="length"/> bytes from <paramref name="source"/> to the next available node for writing
        /// </summary>
        /// <param name="source">Pointer to the buffer to copy</param>
        /// <param name="timeout">The maximum number of milliseconds to wait for a node to become available (default 1000ms)</param>
        /// <param name="length">The number of bytes to attempt to write</param>
        /// <returns>The number of bytes written</returns>
        /// <remarks>The maximum number of bytes that can be written is the minimum of <paramref name="length"/> and <see cref="NodeBufferSize"/>.</remarks>        
        public virtual int Write(IntPtr source, int length)
        {
            // Grab a node for writing
            Node* node = GetNodeForWriting();
            if (node == null) return 0;

            // Copy the data
            int amount = Math.Min(length, NodeBufferSize);
            base.Write(source, amount, node->Offset);
            node->AmountWritten = amount;

            // Writing is complete, make readable
            PostNode(node);

            return amount;
        }

        /// <summary>
        /// Reserves a node for writing and then calls the provided <paramref name="writeFunc"/> to perform the write operation.
        /// </summary>
        /// <param name="writeFunc">A function to used to write to the node's buffer. The first parameter is a pointer to the node's buffer. 
        /// The provided function should return the number of bytes written.</param>
        /// <returns>The number of bytes written</returns>
        public virtual int Write(Func<IntPtr, int> writeFunc)
        {
            // Grab a node for writing
            Node* node = GetNodeForWriting();
            if (node == null) return 0;

            int amount = 0;
            try
            {
                // Pass destination IntPtr to custom write function
                amount = writeFunc(new IntPtr(BufferStartPtr + node->Offset));
                node->AmountWritten = amount;
            }
            finally
            {
                // Writing is complete, make readable
                PostNode(node);
            }

            return amount;
        }

        #endregion

      }
}
