using System;
using System.IO;

namespace ExternalSorting.ConsoleApp
{
    public static class StreamExtensions
    {
        public static void CopyTo(this Stream input, Stream output)
        {
            if (input == null)
            {
                throw new ArgumentNullException("input");
            }
            if (output == null)
            {
                throw new ArgumentNullException("output");
            }
            byte[] buffer = new byte[8192];
            int bytesRead;
            while ((bytesRead = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, bytesRead);
            }
        }
    }
}
