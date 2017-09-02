using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ExternalSorting
{
    public class BigFile : IFile
    {
        static Random Rnd = new Random();

        int tapes;

        int lengthOfRun;

        int subFilesCount;
        
        ulong countOfLines;

        string filenameTemplate;

        string path;

        string inputFilename;

        string outputFilename;

        string templateFilenameForSorting;

        List<MyRow> tapeSources = new List<MyRow>();

        public string[] SetOfWords = new[] {
            "Apple", "Green Apple", "Big Red Apple",
            "Testimony", "Testing", "Integration",
            "Something new", "Something innovation", "Something incredible",
            "Cherry is red", "Cherry is the best", "Cherry is something incredible",
            "Quest" , "New Quest",
            "Banana is yellow", "Banana is yellow submarine"
        };

        public void Init(string path, string filenameTemplate, int subFilesCount, ulong countOfLines, int tapes, int lengthOfRun)
        {
            this.path = path;
            this.filenameTemplate = filenameTemplate;
            this.subFilesCount = subFilesCount;
            this.countOfLines = countOfLines;
            this.tapes = tapes;
            this.lengthOfRun = lengthOfRun;

            CheckParams();

            DirectoryInfo d = new DirectoryInfo(path);
            FileInfo[] files = d.GetFiles("*.txt");
            files.AsParallel().ForAll(x => { x.Delete(); });

            this.inputFilename = Path.Combine(path, "input-" + filenameTemplate + ".txt");
            this.filenameTemplate = Path.Combine(path, filenameTemplate + "{0}.txt");
            this.outputFilename = Path.Combine(path, "output-" + filenameTemplate + ".txt");
            this.templateFilenameForSorting = Path.Combine(path, "t" + filenameTemplate + "{0}.txt");
        }

        public void CreateBigFile()
        {
            CheckParams();

            //create files and data for each file
            //List<Task> list = new List<Task>();
            for (int item = 0; item < subFilesCount; item++)
            {
                string filenameTemplateFormated = string.Format(filenameTemplate, item);
                CreateSubFile(path, filenameTemplateFormated, countOfLines);
                //var task = Task.Factory.StartNew(() => );
                //list.Add(task);
            }

            //Task.WaitAll(list.ToArray());

            string filenamePath = Path.Combine(path, inputFilename);
            if (File.Exists(filenamePath))
            {
                File.Delete(filenamePath);
            }

            //merge files
            for (int item = 0; item < subFilesCount; item++)
            {
                string filenameTemplateFormated = string.Format(filenameTemplate, item);
                //string filenameTemplatePath = Path.Combine(path, filenameTemplateFormated);

                using (Stream input = File.OpenRead(filenameTemplateFormated))
                using (Stream output = new FileStream(filenamePath, FileMode.Append, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                }
                File.Delete(filenameTemplateFormated);

                //File.AppendAllText(filenamePath, File.ReadAllText(filenameTemplatePath));
            }
        }

        public void Sort()
        {
            CheckParams();
            
            for (var tape = 0; tape < this.tapes; tape++)
            {
                string filenameFormated = string.Format(filenameTemplate, tape);
                using (var output = File.Open(filenameFormated, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Write));
                tapeSources.Add(new MyRow() { FileName = filenameFormated });
            }

            DivideOnTapes();

            MergeTapes();
        }

        private void DivideOnTapes()
        {
            var countOfItems = File.ReadLines(inputFilename).Count();
            var steps = countOfItems / (tapes * lengthOfRun);
            steps = (countOfItems % (tapes * lengthOfRun)) > 0 ? steps + 1 : steps;

            var position = 0;
            var maxLengthOfRun = lengthOfRun;

            using (var fileStream = File.OpenRead(inputFilename))
            using (var streamReader = new StreamReader(fileStream))
            {
                try
                {
                    //open streams
                    for (var tape = 0; tape < tapes; tape++)
                    {
                        var streamWriter = new StreamWriter(tapeSources[tape].FileName);
                        tapeSources[tape].StreamWriter = streamWriter;
                    }

                    for (var step = 0; step < steps; step++)
                    {
                        for (var tape = 0; tape < tapes; tape++)
                        {
                            List<MyRow> temporaryList = new List<MyRow>();
                            for (var itemOfRun = position; itemOfRun < maxLengthOfRun; itemOfRun++)
                            {
                                if (itemOfRun == countOfItems)
                                {
                                    temporaryList.Sort();
                                    temporaryList.ForEach(x =>
                                    {
                                        string formatedRow = new StringBuilder().AppendFormat("{0}. {1}", x.Number, x.Text.Trim()).ToString();
                                        tapeSources[tape].StreamWriter.WriteLine(formatedRow);
                                    });
                                    return;
                                }

                                string line = streamReader.ReadLine();
                                if (!string.IsNullOrEmpty(line))
                                {
                                    var items = line.Split('.');
                                    ulong number;
                                    ulong.TryParse(items[0], out number);
                                    temporaryList.Add(new MyRow() { Number = number, Text = items[1].Trim() });
                                }
                            }

                            temporaryList.Sort();

                            temporaryList.ForEach(x =>
                            {
                                string formatedRow = new StringBuilder().AppendFormat("{0}. {1}", x.Number, x.Text.Trim()).ToString();
                                tapeSources[tape].StreamWriter.WriteLine(formatedRow);
                            });

                            maxLengthOfRun = maxLengthOfRun + lengthOfRun;
                            position = position + lengthOfRun;
                        }
                    }
                }
                finally
                {
                    //close streams
                    for (var tape = 0; tape < tapes; tape++)
                    {
                        tapeSources[tape].StreamWriter.Close();
                        tapeSources[tape].StreamWriter.Dispose();
                    }
                }
            }
        }

        private void MergeTapes()
        {
            ConcurrentDictionary<int, MyRow> temporarySources = new ConcurrentDictionary<int, MyRow>();
            
            int temporaryTapes = tapes;
            int maxLengthOfRun = lengthOfRun;
            int tempLengthOfRun = lengthOfRun;

            while (true)
            {
                var tapeSourcesCount = tapeSources.Count();
                tempLengthOfRun = maxLengthOfRun;
                maxLengthOfRun = maxLengthOfRun * tapeSourcesCount;

                try
                {
                    //open streams
                    foreach(var item in tapeSources)
                    {
                        item.CountOfRows = File.ReadLines(item.FileName).Count();
                        item.NextPosition = 0;
                        var streamReader = new StreamReader(item.FileName);
                        item.StreamReader = streamReader;
                    }

                    temporarySources = new ConcurrentDictionary<int, MyRow>();
                    for (var tape = 0; tape < temporaryTapes; tape++)
                    {
                        string filenameFormated = string.Format(templateFilenameForSorting, tape);
                        var streamWriter = new StreamWriter(filenameFormated);
                        temporarySources.TryAdd(tape, new MyRow() { FileName = filenameFormated, StreamWriter = streamWriter });
                    }

                    bool needIteration = true;
                    while (needIteration)
                    {
                        for (var tape = 0; tape < temporaryTapes; tape++)
                        {
                            List<MyRow> sortedList = new List<MyRow>();

                            for (var item = 0; item < tapeSourcesCount; item++)
                            {
                                tapeSources[item].LeftItemsInRun = tempLengthOfRun;
                                tapeSources[item].NextPosition = tapeSources[item].NextPosition + 1;

                                string line = tapeSources[item].StreamReader.ReadLine();
                                if (!string.IsNullOrEmpty(line))
                                {
                                    var items = line.Split('.');
                                    ulong number;
                                    ulong.TryParse(items[0], out number);

                                    tapeSources[item].Number = number;
                                    tapeSources[item].Text = items[1].Trim();
                                    sortedList.Add(tapeSources[item]);
                                }
                            }

                            for (var itemOfRun = 0; itemOfRun < maxLengthOfRun && sortedList.Count > 0; itemOfRun++)
                            {
                                //select min
                                if (sortedList.Count() == 0)
                                    break;

                                sortedList.Sort();
                                var sortedItem = sortedList.FirstOrDefault();

                                string formatedRow = new StringBuilder().AppendFormat("{0}. {1}", sortedItem.Number, sortedItem.Text).ToString();
                                temporarySources[tape].StreamWriter.WriteLine(formatedRow);
                                temporarySources[tape].StreamWriter.Flush();

                                sortedItem.LeftItemsInRun = sortedItem.LeftItemsInRun - 1;
                                sortedList.Remove(sortedItem);

                                if (sortedItem.CountOfRows > sortedItem.NextPosition
                                    && sortedItem.LeftItemsInRun >= 1)
                                {
                                    string line = sortedItem.StreamReader.ReadLine();
                                    if (!string.IsNullOrEmpty(line))
                                    {
                                        var items = line.Split('.');
                                        ulong number;
                                        ulong.TryParse(items[0], out number);
                                        
                                        sortedItem.Number = number;
                                        sortedItem.Text = items[1].Trim();
                                        sortedItem.NextPosition = sortedItem.NextPosition + 1;
                                        sortedList.Add(sortedItem);
                                    }
                                }
                            }

                            if (tapeSources.Count(x => (x.CountOfRows - x.NextPosition) > 0) == 1)
                            {
                                var tapeSource = tapeSources.Where(x => (x.CountOfRows - x.NextPosition) > 0).FirstOrDefault();
                                //get last tape to add items from last run
                                if ((tapeSource.CountOfRows - tapeSource.NextPosition) <= tempLengthOfRun)
                                {
                                    int pos = tapeSources.Count == 2 ? 1 : tapeSources.Count - 1;
                                    while ((tapeSource.CountOfRows - tapeSource.NextPosition) > 0)
                                    {
                                        temporarySources[pos].StreamWriter.WriteLine(tapeSource.StreamReader.ReadLine());
                                        temporarySources[pos].StreamWriter.Flush();
                                        tapeSource.NextPosition = tapeSource.NextPosition + 1;
                                    }
                                }
                                break;
                            }

                            needIteration = tapeSources.Select(x => (x.CountOfRows - x.NextPosition)).Any(x => x > 0);
                            if (!needIteration)
                                break;
                        }

                        needIteration = tapeSources.Select(x => (x.CountOfRows - x.NextPosition)).Any(x => x > 0);
                    }
                }
                finally
                {
                    //close streams
                    for (var tape = 0; tape < temporaryTapes; tape++)
                    {
                        temporarySources[tape].StreamWriter.Close();
                        temporarySources[tape].StreamWriter.Dispose();
                        tapeSources[tape].StreamReader.Close();
                        tapeSources[tape].StreamReader.Dispose();

                        File.Delete(tapeSources[tape].FileName);
                        File.Move(temporarySources[tape].FileName, tapeSources[tape].FileName);
                        File.Delete(temporarySources[tape].FileName);
                    }

                    var itemsToRemove = tapeSources.Where(x => File.ReadLines(x.FileName).Count() == 0).ToList();

                    if (itemsToRemove.Count > 0)
                    {
                        var filesToRemove = itemsToRemove.Select(x => x.FileName).ToArray();
                        tapeSources.RemoveAll(x => filesToRemove.Contains(x.FileName));
                        foreach (var item in itemsToRemove)
                        {
                            File.Delete(item.FileName);
                            temporaryTapes = temporaryTapes - 1;
                            tapeSourcesCount = tapeSourcesCount - 1;
                        }
                    }
                }

                if (tapeSources.Count == 1)
                {
                    var tapeSource = tapeSources.FirstOrDefault();

                    File.Move(tapeSource.FileName, outputFilename);
                    File.Delete(tapeSource.FileName);
                    break;
                }
            }
        }
        
        private void CreateSubFile(string path, string filenameTemplate, ulong countOfLines)
        {
            using (var output = File.Open(filenameTemplate, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Write))
            using (var textStream = new StreamWriter(output))
            {
                for (ulong line = 1; line <= countOfLines; line++)
                {
                    ulong random = Rnd.NextULong(0, line);
                    int position = Rnd.Next(0, SetOfWords.Count());
                    string word = SetOfWords[position];
                    string formatedRow = new StringBuilder().AppendFormat("{0}. {1}", random, word.Trim()).ToString();

                    textStream.WriteLine(formatedRow);
                }
            }
        }

        private void CheckParams()
        {
            if (subFilesCount <= 0)
                throw new ArgumentException("subFilesCount should not be more then 0");

            if (countOfLines <= 1)
                throw new ArgumentException("countOfLines should not be more then one");

            if (tapes <= 1)
                throw new ArgumentException("tapes should not be more then 1");

            if (lengthOfRun <= 0)
                throw new ArgumentException("lengthOfRun should not be more then 0");

            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("path should not be empty");

            if (string.IsNullOrEmpty(filenameTemplate))
                throw new ArgumentException("filenameTemplate should not be empty");

            DirectoryInfo d = new DirectoryInfo(path);
            bool directory = d.Exists;
            if (!d.Exists)
                throw new ArgumentException("directory does not exists :" + path);
        }
    }
}
