using System;
using System.Collections.Generic;
using System.Text;

namespace ServerCore.Utile
{
    public sealed class TeeTextWriter : TextWriter
    {
        private readonly TextWriter _console;
        private readonly TextWriter _file;
        private readonly object _lock = new();

        public override Encoding Encoding => _console.Encoding;

        public TeeTextWriter(TextWriter console, TextWriter file)
        {
            _console = console;
            _file = file;
        }

        public override void WriteLine(string? value)
        {
            lock (_lock)
            {
                _console.WriteLine(value);
                _file.WriteLine(value);
                _file.Flush();
            }
        }

        public override void Write(char value)
        {
            lock (_lock)
            {
                _console.Write(value);
                _file.Write(value);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _file.Dispose();

            base.Dispose(disposing);
        }
    }
}
