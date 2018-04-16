// SharedMemory (File: ServerTest\Program.cs)
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
#if NET40Plus
using System.Threading.Tasks;
using static SharedMemory.DAQBuffer;
#endif

namespace ServerDaqTest
{
    class Program
    {
        static void Main(string[] args)
        {
            int bufferSize = 1048576;
            int size = sizeof(byte) * bufferSize;
            int count = 10;
            Console.WriteLine("Generate data to be written");

            // Generate data to be written
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
            Console.WriteLine("Press Enter to start.");

            Console.WriteLine("Create shared memory circular buffer");
            using (var theServer = new SharedMemory.DAQBufferWriter("TEST", count, size))
            {
                Console.WriteLine("DAQBufferWriter created.");
 
                int skipCount = 0;
                Stopwatch sw = Stopwatch.StartNew();                
                Action writer = () =>
                {
                    int linesOut = 0;
                    for (;;)
                    {
                        var counter = theServer.WriteCounter;
                        var start = sw.ElapsedTicks;
                        int amount = theServer.Write(dataList[counter % 255]);
                        var ticks = sw.ElapsedTicks - start;
                        var diag = theServer.DiagInfo;

                        if (amount == 0)
                        {
                            throw new Exception("no data written!");
                        }
                        Console.WriteLine(diag.ToString());
                        Console.WriteLine("Write: {0}, {1} MB/s", 
                            ((double)amount / 1048576.0).ToString("F0"), (((amount / 1048576.0) / ticks) * 10000000).ToString("F0"));
                        linesOut++;
                        if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
                        {
                            break;
                        }
                        Thread.Sleep(100);
                    }
                };
                Console.WriteLine("Press Esc Key to exit loop.");
                writer();
                Console.WriteLine("Press Emter to exit.");
                Console.ReadLine();
            }
        }
    }
}
