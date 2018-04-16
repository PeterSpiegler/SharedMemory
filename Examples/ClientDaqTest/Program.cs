// SharedMemory (File: ClientTest\Program.cs)
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
using System.Linq;
using System.Text;
using System.Threading;
using SharedMemory;

#if NET40Plus
using System.Threading.Tasks;
#endif

namespace ClientTest
{
    class Program
    {
        static void Main(string[] args)
        {
            int bufferSize = 1048576;
            byte[] writeData = new byte[bufferSize];
            int size = sizeof(byte) * bufferSize;
            int count = 10;
            Console.WriteLine("Generate data to be proof");

            // Generate data to be proof
            byte[][] dataList = new byte[256][];
            for (var j = 0; j < 256; j++)
            {
                var data = new byte[bufferSize];
                for (var i = 0; i < data.Length; i++)
                {
                    data[i] = (byte)((i + j) % 255);
                }
                dataList[j] = data;
            }

            Console.WriteLine("Press <enter> to start client");
            do
            {
                try
                {

                    

                    //Console.ReadLine();



                    int skipCount = 0;
                    long iterations = 0;
                    Stopwatch sw = Stopwatch.StartNew();

                    int threadCount = 0;
                    Action reader = () =>
                    {
                        using (var theClient = new SharedMemory.DAQBufferReader("TEST"))
                        {
                            if (bufferSize != theClient.NodeBufferSize)
                                throw new Exception("buffersize mismatch");


                            int myThreadIndex = Interlocked.Increment(ref threadCount);
                            Console.WriteLine("Thread {3}: Buffer {0} opened, NodeBufferSize: {1}, NodeCount: {2}", theClient.Name, theClient.NodeBufferSize, theClient.NodeCount, myThreadIndex);
                            for (;;)
                            {
                                var start = sw.ElapsedTicks;
                                int amount = theClient.Read(writeData);
                                var ticks = sw.ElapsedTicks - start;
                                var diag = theClient.DiagInfo;
                                ticks -= diag.waitticks;

                                if (amount == 0)
                                {
                                    Interlocked.Increment(ref skipCount);
                                }
                                else
                                {
                                    Interlocked.Increment(ref iterations);
                                }

                                bool mismatch = false;
                                var writeDataProof = dataList[diag.ReadNode.ContinueCounter % 255];
                                for (var i = 0; i < writeDataProof.Length; i++)
                                {
                                    if (writeData[i] != writeDataProof[i])
                                    {
                                        mismatch = true;
                                        throw new Exception("Buffers don't match!");
                                        //break;
                                    }
                                }



                                Console.WriteLine(diag.ToString());
                                //Console.WriteLine("Thread {3}, Read: {0}, Wait: {1}, {2} MB/s",((double)amount / 1048576.0).ToString("F0"), skipCount, (((amount / 1048576.0) / ticks) * 10000000).ToString("F0"), myThreadIndex);
                                if (Console.KeyAvailable)
                                {
                                    var keyinfo = Console.ReadKey(true);
                                    if (keyinfo.Key == ConsoleKey.Escape)
                                        break;
                                    if (keyinfo.Key == ConsoleKey.Spacebar)
                                    {
                                        Console.WriteLine("Wait...  Press a key to continue");
                                        keyinfo = Console.ReadKey();

                                    }

                                }
                            }
                        }

                    };

                    Console.WriteLine("Press: ESC: end, Space: pause");
                    reader();

                }
                catch (Exception ex)
                {
                    Console.WriteLine("Exception occured {0}", ex.ToString());
                }
                Console.WriteLine("Press: ESC: end, any other key restart");
                if (Console.ReadKey().Key == ConsoleKey.Escape)
                    break;

            } while (true) ; 
        }
    }
}

