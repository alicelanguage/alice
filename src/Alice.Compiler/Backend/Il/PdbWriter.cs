using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Security.Cryptography;
using System.Text;

namespace Alice.Compiler.Backend.Il;

/// <summary>
/// The portable PDB of a module (Portable PDB v1.0): Document rows (SHA-256 of the source when known, Alice
/// language GUID), one MethodDebugInformation row per MethodDef with the sequence-point blob, LocalScope rows with
/// LocalVariable names, a root ImportScope, Source Link JSON and embedded sources as CustomDebugInformation.
/// </summary>
internal static class PdbWriter
{
    public const ushort PortablePdbVersion = 0x0100;

    static readonly Guid Sha256 = new("8829d00f-11b8-4213-878b-770e8597ac16");
    static readonly Guid SourceLinkKind = new("cc110556-a091-4d38-9fec-25ab9a351a6a");
    static readonly Guid EmbeddedSourceKind = new("0e8a571b-6926-466e-b4ad-8ab04611f5fe");

    public static (BlobBuilder Blob, BlobContentId Id) Build(IlModule module, IReadOnlyList<IlMethodDef> methods,
        IReadOnlyList<IlEncodedBody?> bodies, ImmutableArray<int> rowCounts, MethodDefinitionHandle entryPoint,
        Func<IEnumerable<Blob>, BlobContentId> idProvider)
    {
        var pdb = new MetadataBuilder();
        var docs = new Dictionary<IlDocument, DocumentHandle>();
        var embedded = new List<(DocumentHandle, BlobHandle)>();
        foreach (var d in module.Documents)
        {
            var bytes = d.Source != null ? Encoding.UTF8.GetBytes(d.Source) : null;
            var h = pdb.AddDocument(pdb.GetOrAddDocumentName(d.Path),
                bytes != null ? pdb.GetOrAddGuid(Sha256) : default,
                bytes != null ? pdb.GetOrAddBlob(SHA256.HashData(bytes)) : default,
                pdb.GetOrAddGuid(d.Language));
            docs[d] = h;
            if (d.Embed && bytes != null) embedded.Add((h, pdb.GetOrAddBlob(EmbeddedSource(bytes))));
        }

        // CustomDebugInformation is sorted by parent: the module (coded 0x27) precedes documents (row << 5 | 22)
        if (module.SourceLinkJson != null)
            pdb.AddCustomDebugInformation(EntityHandle.ModuleDefinition, pdb.GetOrAddGuid(SourceLinkKind), pdb.GetOrAddBlob(Encoding.UTF8.GetBytes(module.SourceLinkJson)));
        foreach (var (h, blob) in embedded) pdb.AddCustomDebugInformation(h, pdb.GetOrAddGuid(EmbeddedSourceKind), blob);

        var rootImports = pdb.AddImportScope(default, pdb.GetOrAddBlob(Array.Empty<byte>()));

        for (int i = 0; i < methods.Count; i++)
        {
            var body = bodies[i];
            if (body == null || !body.SequencePoints.Any(s => s.Document != null)) { pdb.AddMethodDebugInformation(default, default); continue; }
            var first = body.SequencePoints.FirstOrDefault(s => s.Document != null).Document;
            bool single = body.SequencePoints.All(s => s.Document == null || s.Document == first);
            pdb.AddMethodDebugInformation(single ? docs[first!] : default,
                pdb.GetOrAddBlob(SequencePoints(body, docs, single, first)));
        }

        int nextVar = 1;
        for (int i = 0; i < methods.Count; i++)
        {
            var body = bodies[i];
            if (body == null) continue;
            var mh = MetadataTokens.MethodDefinitionHandle(i + 1);
            foreach (var s in body.Scopes)
            {
                pdb.AddLocalScope(mh, rootImports, MetadataTokens.LocalVariableHandle(nextVar), MetadataTokens.LocalConstantHandle(1), s.Start, s.Length);
                foreach (var l in s.Locals)
                {
                    pdb.AddLocalVariable(LocalVariableAttributes.None, l.Index, pdb.GetOrAddString(l.Name!));
                    nextVar++;
                }
            }
        }

        var builder = new PortablePdbBuilder(pdb, rowCounts, entryPoint, idProvider);
        var output = new BlobBuilder();
        var id = builder.Serialize(output);
        return (output, id);
    }

    /// <summary>Embedded source blob: int32 0 + raw text, or the uncompressed size + deflate when that is smaller.</summary>
    static byte[] EmbeddedSource(byte[] text)
    {
        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(text);
        var packed = ms.ToArray();
        bool compress = packed.Length < text.Length;
        var payload = compress ? packed : text;
        var r = new byte[4 + payload.Length];
        BitConverter.TryWriteBytes(r.AsSpan(0, 4), compress ? text.Length : 0);
        payload.CopyTo(r, 4);
        return r;
    }

    /// <summary>The sequence-point blob (Portable PDB spec, "SequencePoints Blob").</summary>
    static BlobBuilder SequencePoints(IlEncodedBody body, Dictionary<IlDocument, DocumentHandle> docs, bool single, IlDocument? first)
    {
        var b = new BlobBuilder();
        b.WriteCompressedInteger(body.LocalSignature.IsNil ? 0 : MetadataTokens.GetRowNumber(body.LocalSignature));
        var current = first;
        if (!single) b.WriteCompressedInteger(MetadataTokens.GetRowNumber(docs[first!]));
        int prevOffset = -1, prevLine = 0, prevCol = 0;
        bool haveVisible = false;
        foreach (var s in body.SequencePoints)
        {
            if (s.Document != null && s.Document != current)
            {
                b.WriteCompressedInteger(0);
                b.WriteCompressedInteger(MetadataTokens.GetRowNumber(docs[s.Document]));
                current = s.Document;
            }
            b.WriteCompressedInteger(prevOffset < 0 ? s.Offset : s.Offset - prevOffset);
            prevOffset = s.Offset;
            if (s.Document == null)
            {
                b.WriteCompressedInteger(0);
                b.WriteCompressedInteger(0);
                continue;
            }
            int dl = s.EndLine - s.StartLine, dc = s.EndColumn - s.StartColumn;
            b.WriteCompressedInteger(dl);
            if (dl == 0) b.WriteCompressedInteger(dc); else b.WriteCompressedSignedInteger(dc);
            if (!haveVisible)
            {
                b.WriteCompressedInteger(s.StartLine);
                b.WriteCompressedInteger(s.StartColumn);
                haveVisible = true;
            }
            else
            {
                b.WriteCompressedSignedInteger(s.StartLine - prevLine);
                b.WriteCompressedSignedInteger(s.StartColumn - prevCol);
            }
            prevLine = s.StartLine;
            prevCol = s.StartColumn;
        }
        return b;
    }
}
