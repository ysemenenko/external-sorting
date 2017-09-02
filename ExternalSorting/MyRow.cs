using System;
using System.IO;

namespace ExternalSorting
{
    public class MyRow : IEquatable<MyRow>, IComparable<MyRow>
    {
        public MyRow()
        {
        }

        public ulong Number { get; set; }

        public string Text { get; set; }

        //public int FirstValue { get; set; }

        public string FileName { get; set; }

        public StreamWriter StreamWriter { get; set; }

        public StreamReader StreamReader { get; set; }

        public int LeftItemsInRun { get; set; }

        public int CountOfRows { get; set; }

        public int NextPosition { get; set; }

        public override bool Equals(object obj)
        {
            return this.Equals(obj as MyRow);
        }

        public bool Equals(MyRow other)
        {
            if (other == null)
                return false;

            return this == other;
        }

        public static bool operator !=(MyRow x, MyRow y)
        {
            return !(x == y);
        }

        public static bool operator ==(MyRow x, MyRow y)
        {
            // If both are null, or both are same instance, return true.
            if (Object.ReferenceEquals(x, y))
            {
                return true;
            }

            // If one is null, but not both, return false.
            if (((object)x == null) || ((object)y == null))
            {
                return false;
            }

            // Return true if the fields match:
            return (x.Number == y.Number) && (x.Text == y.Text);
        }

        public override int GetHashCode()
        {
            return Number.GetHashCode() ^ Text.GetHashCode();
        }

        public int CompareTo(MyRow other)
        {
            var result = this.Text.CompareTo(other.Text);
            if (result == 0) result = this.Number.CompareTo(other.Number);
            return result;
        }
    }
}
