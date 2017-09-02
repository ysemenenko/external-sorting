using System;

namespace ExternalSorting
{
    public static class FileProcessorFactory
    {
        public static IFile GetSorter(SortingType sortingType)
        {
            switch (sortingType)
            {
                case SortingType.ExternalTwoWaySorting:
                    return new BigFile();
                default:
                    throw new NotImplementedException("Unknown sorting type: " + sortingType);
            }
        }
    }
}
