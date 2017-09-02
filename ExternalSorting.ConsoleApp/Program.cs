using System;
using System.IO;

namespace ExternalSorting.ConsoleApp
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine(string.Format("Please enter number of files which will used to generate input file."));
            Console.WriteLine(string.Format("Use <enter> to confirm entering."));
            Console.WriteLine(string.Format("It should be more then 0. If you enter incorrect number, will used default 5"));
            string sSubFilesCount = Console.ReadLine();

            int subFilesCount;
            int.TryParse(sSubFilesCount, out subFilesCount);
            if (subFilesCount <= 0)
                subFilesCount = 5;

            Console.WriteLine(string.Format("Please enter number of rows in file which will used to generate input file."));
            Console.WriteLine(string.Format("Use <enter> to confirm entering."));
            Console.WriteLine(string.Format("It should be more then 0. If you enter incorrect number, will used default 999"));
            string sСountRowsInFile = Console.ReadLine();

            ulong countRowsInFile;
            ulong.TryParse(sСountRowsInFile, out countRowsInFile);
            if (countRowsInFile <= 0)
                countRowsInFile = 999;

            Console.WriteLine(string.Format("Please enter number of tapes. Use <enter> to confirm entering."));
            Console.WriteLine(string.Format("It should be more then 0. If you enter incorrect number, will used default 3"));
            string sTapes = Console.ReadLine();

            int tapes;
            int.TryParse(sTapes, out tapes);
            if (tapes <= 0)
                tapes = 3;

            Console.WriteLine(string.Format("Please enter length of run. Use <enter> to confirm entering."));
            Console.WriteLine(string.Format("It should be more then 0. If you enter incorrect number, will used default 4"));
            string sLengthOfRun = Console.ReadLine();

            int lengthOfRun;
            int.TryParse(sLengthOfRun, out lengthOfRun);
            if (lengthOfRun <= 0)
                lengthOfRun = 4;
            
            string filenameTemplate = "file";
            string path = AppDomain.CurrentDomain.BaseDirectory;
            Console.WriteLine(string.Format("Start processing ... "));
            Console.WriteLine(string.Format(".................... "));

            Console.WriteLine(string.Format("Output path of files: " + path));

            IFile externalSorting = FileProcessorFactory.GetSorter(SortingType.ExternalTwoWaySorting);
            var watch = System.Diagnostics.Stopwatch.StartNew();

            externalSorting.Init(path, filenameTemplate, subFilesCount, countRowsInFile, tapes, lengthOfRun);

            Console.WriteLine(string.Format("Creating big file, please WAIT until finishing process ... "));
            //create file for sorting
            externalSorting.CreateBigFile();

            var elapsedMs = watch.ElapsedMilliseconds;
            Console.WriteLine(string.Format("Created big file, Elapsed Milliseconds {0} ms", elapsedMs));

            Console.WriteLine(string.Format("Sorting big file, please WAIT until finishing process ... "));
            //sorting
            externalSorting.Sort();

            watch.Stop();
            elapsedMs = watch.ElapsedMilliseconds;
            Console.WriteLine(string.Format("Sorted big file, Elapsed Milliseconds {0} ms", elapsedMs));

            Console.WriteLine("Press any key to stop...");
            Console.Read();
        }
    }
}
