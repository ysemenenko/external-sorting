namespace ExternalSorting
{
    public interface IFile
    {
        void Init(string path, string filenameTemplate, int tasks, ulong countOfLines, int tapes, int lengthOfRun);

        void CreateBigFile();

        void Sort();
    }
}
