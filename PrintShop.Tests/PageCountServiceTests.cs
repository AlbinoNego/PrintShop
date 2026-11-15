using System.IO.Compression;
using System.Text;
using PrintShop.Services;

namespace PrintShop.Tests;

public class PageCountServiceTests : IDisposable
{
    private readonly TestStorage _storage = new();
    private readonly PageCountService _counter = new();

    [Fact]
    public void Conta_paginas_de_pdf_comum()
    {
        var pages = string.Concat(Enumerable.Range(3, 3).Select(n => $"{n} 0 obj\n<< /Type /Page /Parent 2 0 R >>\nendobj\n"));
        var path = Write("comum.pdf", Encoding.Latin1.GetBytes(
            "%PDF-1.4\n2 0 obj\n<< /Type /Pages /Count 3 >>\nendobj\n" + pages + "%%EOF"));

        Assert.Equal(3, _counter.CountPages(path, "comum.pdf"));
    }

    [Fact]
    public void Conta_paginas_comprimidas_em_object_stream()
    {
        var inner = string.Concat(Enumerable.Repeat("<< /Type /Page /Parent 2 0 R >> ", 7));
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(Encoding.Latin1.GetBytes(inner));
        }
        var header = Encoding.Latin1.GetBytes(
            "%PDF-1.7\n5 0 obj\n<< /Type /ObjStm /N 7 /First 10 /Filter /FlateDecode /Length " + compressed.Length + " >>\nstream\n");
        var footer = Encoding.Latin1.GetBytes("\nendstream\nendobj\n%%EOF");
        var path = Write("objstm.pdf", header.Concat(compressed.ToArray()).Concat(footer).ToArray());

        Assert.Equal(7, _counter.CountPages(path, "objstm.pdf"));
    }

    [Fact]
    public void Imagem_conta_uma_pagina()
    {
        var path = Write("foto.png", new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        Assert.Equal(1, _counter.CountPages(path, "foto.png"));
    }

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_storage.Root, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    public void Dispose() => _storage.Dispose();
}
