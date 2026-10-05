using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfEditor.Core.Pdf;
using PdfEditor.Core.Pdf.Fonts;

namespace PdfEditor.Core;

/// <summary>
/// Electronic signatures: a hand-drawn/uploaded signature image stamped onto the page,
/// and cryptographic digital signatures from a PKCS#12 (.pfx/.p12) certificate.
/// </summary>
public static class Signer
{
    /// <summary>Bytes reserved for the CMS signature (a certificate chain fits with room to spare).</summary>
    private const int SignatureSpace = 16384;

    /// <summary>Stamps a signature image (e.g. drawn on the extension's signature pad) onto a page.</summary>
    public static byte[] AddImageSignature(byte[] pdf, RectRegion placement, byte[] imageBytes,
        string? password = null)
    {
        var doc = PdfIo.Open(pdf, password);
        var page = doc.GetPage(placement.Page);
        var (image, _, _) = PdfImages.CreateXObject(imageBytes);
        var name = PdfResources.Add(page.GetOrCreateResources(), PdfName.XObject, "Im", image);
        var canvas = new ContentBuilder().SaveState()
            .Transform(placement.Width, 0, 0, placement.Height, placement.X, placement.Y)
            .DrawXObject(name).RestoreState();
        PdfContentGuard.DrawInDefaultUserSpace(page, canvas.ToArray());
        return PdfIo.Save(doc);
    }

    /// <summary>
    /// Applies a cryptographic digital signature. When <paramref name="placement"/> is given the
    /// signature is visible on that page; otherwise it is invisible.
    /// <para>
    /// The signature is appended as an incremental update: every byte of the document before it is
    /// left exactly as it was, so signatures already in the document stay valid, and an encrypted
    /// document stays encrypted under its own key.
    /// </para>
    /// </summary>
    public static byte[] SignDigitally(byte[] pdf, byte[] pkcs12, string pkcs12Password,
        string? reason = null, string? location = null, RectRegion? placement = null,
        byte[]? appearanceImage = null, string? pdfPassword = null)
    {
        using var certificate = LoadPkcs12(pkcs12, pkcs12Password, out var chain);
        string subjectName = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        if (string.IsNullOrWhiteSpace(subjectName)) subjectName = "Unknown signer";

        var doc = PdfIo.Open(pdf, pdfPassword);
        var changed = new List<PdfObject>();
        var catalog = doc.Catalog!;

        // The signature dictionary, with fixed-width placeholders patched once the bytes are final.
        string contentsPlaceholder = "<" + new string('0', SignatureSpace * 2) + ">";
        const string byteRangePlaceholder = "[0 0000000000 0000000000 0000000000]";
        var signature = doc.MakeIndirect(new PdfDictionary());
        signature.Put(PdfName.Type, PdfName.Sig);
        signature.Put(PdfName.Filter, PdfName.Of("Adobe.PPKLite"));
        signature.Put(PdfName.SubFilter, PdfName.Of("adbe.pkcs7.detached"));
        signature.Put(PdfName.M, new PdfString(Encoding.ASCII.GetBytes(PdfDocument.FormatDate(DateTimeOffset.Now))));
        if (!string.IsNullOrEmpty(reason)) signature.Put(PdfName.Reason, PdfString.FromText(reason));
        if (!string.IsNullOrEmpty(location)) signature.Put(PdfName.Of("Location"), PdfString.FromText(location));
        signature.Put(PdfName.ByteRange, new PdfLiteral(byteRangePlaceholder));
        signature.Put(PdfName.Contents, new PdfLiteral(contentsPlaceholder));

        int pageNumber = placement?.Page ?? 1;
        if (pageNumber < 1 || pageNumber > doc.PageCount)
            throw new ArgumentOutOfRangeException(nameof(placement), $"Page {pageNumber} does not exist.");
        var page = doc.GetPage(pageNumber);
        var rect = placement == null ? new PdfRect(0, 0, 0, 0)
            : new PdfRect(placement.X, placement.Y, placement.Width, placement.Height);

        var field = AcroForm.NewWidget(rect, styled: false);
        field.Put(PdfName.F, new PdfNumber(132)); // print + locked
        field.Put(PdfName.FT, PdfName.Sig);
        field.Put(PdfName.T, PdfString.FromText($"Signature_{DateTime.UtcNow:yyyyMMddHHmmssfff}"));
        field.Put(PdfName.V, signature);
        field.Put(PdfName.P, page.Dictionary);
        doc.MakeIndirect(field);
        if (placement != null)
        {
            var ap = new PdfDictionary();
            ap.Put(PdfName.N, VisibleAppearance(doc, rect, subjectName, appearanceImage));
            field.Put(PdfName.AP, ap);
        }
        changed.Add(field);

        // The page lists the widget...
        changed.Add(AddToArray(page.Dictionary, PdfName.Annots, field));
        // ...and the form lists the field, flagged as holding signatures that an append keeps valid.
        var form = catalog.GetAsDictionary(PdfName.AcroForm);
        if (form == null)
        {
            form = doc.MakeIndirect(new PdfDictionary());
            catalog.Put(PdfName.AcroForm, form);
            changed.Add(catalog);
        }
        changed.Add(form.IsIndirect ? form : catalog);
        changed.Add(AddToArray(form, PdfName.Fields, field));
        form.Put(PdfName.SigFlags, new PdfNumber(3));

        byte[] output = PdfIo.Guarded("signing the document",
            () => doc.SaveIncremental(changed.Distinct(ReferenceEqualityComparer.Instance).Cast<PdfObject>()));
        return Seal(output, contentsPlaceholder, byteRangePlaceholder, certificate, chain);
    }

    /// <summary>
    /// Adds <paramref name="item"/> to the array at <paramref name="owner"/>[<paramref name="key"/>],
    /// creating it when absent, and returns the object an incremental update must rewrite: the
    /// array itself when it is indirect, otherwise its owner.
    /// </summary>
    private static PdfObject AddToArray(PdfDictionary owner, PdfName key, PdfObject item)
    {
        var array = owner.GetAsArray(key);
        if (array == null)
        {
            array = new PdfArray();
            owner.Put(key, array);
        }
        array.Add(item);
        return array.IsIndirect ? array : owner;
    }

    /// <summary>Fills in /ByteRange and /Contents once the update's bytes are fixed.</summary>
    private static byte[] Seal(byte[] output, string contentsPlaceholder, string byteRangePlaceholder,
        X509Certificate2 certificate, X509Certificate2Collection chain)
    {
        byte[] marker = Encoding.ASCII.GetBytes(contentsPlaceholder);
        int contentsStart = output.AsSpan().LastIndexOf(marker);
        int rangeAt = output.AsSpan().LastIndexOf(Encoding.ASCII.GetBytes(byteRangePlaceholder));
        if (contentsStart < 0 || rangeAt < 0)
            throw new InvalidOperationException("The signature placeholders were not found in the written document.");
        int contentsEnd = contentsStart + marker.Length;

        string range = string.Create(CultureInfo.InvariantCulture,
            $"[0 {contentsStart} {contentsEnd} {output.Length - contentsEnd}]");
        range = range.PadRight(byteRangePlaceholder.Length);
        Encoding.ASCII.GetBytes(range).CopyTo(output, rangeAt);

        var signed = new byte[contentsStart + (output.Length - contentsEnd)];
        Array.Copy(output, 0, signed, 0, contentsStart);
        Array.Copy(output, contentsEnd, signed, contentsStart, output.Length - contentsEnd);

        var cms = new SignedCms(new ContentInfo(signed), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), // SHA-256
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        foreach (var extra in chain)
            if (!extra.RawData.AsSpan().SequenceEqual(certificate.RawData)) signer.Certificates.Add(extra);
        signer.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.UtcNow));
        cms.ComputeSignature(signer, silent: true);
        byte[] encoded = cms.Encode();
        if (encoded.Length > SignatureSpace)
            throw new InvalidOperationException("The signature is larger than the space reserved for it.");

        byte[] hex = Encoding.ASCII.GetBytes(Convert.ToHexString(encoded));
        hex.CopyTo(output, contentsStart + 1);
        return output;
    }

    /// <summary>The visible signature: the signer's image (when given) and/or a "signed by" note.</summary>
    private static PdfStream VisibleAppearance(PdfDocument doc, PdfRect rect, string subjectName, byte[]? image)
    {
        float w = rect.Width, h = rect.Height;
        var resources = new PdfDictionary();
        var canvas = new ContentBuilder();
        var font = PdfFont.Standard(StandardFonts.Helvetica);
        var fontName = PdfResources.Add(resources, PdfName.Font, "F", doc.MakeIndirect(font.Dictionary!));
        string[] lines;
        float textWidth = w;
        if (image != null)
        {
            // The image on the left, aspect preserved; the signer's name beside it.
            var (xobject, iw, ih) = PdfImages.CreateXObject(image);
            var imageName = PdfResources.Add(resources, PdfName.XObject, "Im", xobject);
            float box = w / 2;
            float scale = Math.Min(box / iw, h / (float)ih);
            float dw = iw * scale, dh = ih * scale;
            canvas.SaveState().Transform(dw, 0, 0, dh, (box - dw) / 2, (h - dh) / 2).DrawXObject(imageName).RestoreState();
            lines = new[] { subjectName };
            textWidth = w - box;
        }
        else
        {
            lines = new[] { $"Digitally signed by {subjectName}", DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture) };
        }
        float size = Math.Clamp(h / (lines.Length * 1.4f + 0.6f), 4f, 12f);
        foreach (var line in lines)
            while (size > 4f && font.MeasureText(line, size) > textWidth - 4) size -= 0.5f;
        float x = w - textWidth + 2;
        float y = h / 2 + (lines.Length - 1) * size * 0.7f - size * 0.35f;
        canvas.BeginText().FillGray(0).Font(fontName, size).MoveText(x, y);
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) canvas.MoveText(0, -size * 1.4);
            canvas.ShowText(font.Encode(lines[i]));
        }
        canvas.EndText();

        var appearance = new PdfStream(canvas.ToArray());
        appearance.Put(PdfName.Type, PdfName.XObject);
        appearance.Put(PdfName.Subtype, PdfName.Form);
        appearance.Put(PdfName.BBox, new PdfArray(0, 0, w, h));
        appearance.Put(PdfName.Resources, resources);
        return doc.MakeIndirect(appearance);
    }

    /// <summary>Lists the digital signatures in a document and verifies their integrity.</summary>
    public static IReadOnlyList<SignatureInfo> GetSignatures(byte[] pdf, string? password = null)
    {
        var doc = PdfIo.OpenReadOnly(pdf, password);
        var found = new List<(SignatureInfo Info, long Covered)>();
        foreach (var field in AcroForm.TerminalFields(doc))
        {
            if (field.FieldType != "Sig" || field.Value is not PdfDictionary sig) continue;
            var range = sig.GetAsArray(PdfName.ByteRange);
            var contents = sig.GetAsString(PdfName.Contents);
            if (range == null || range.Count < 4 || contents == null) continue;

            long[] r = range.ToDoubleArray().Select(v => (long)v).ToArray();
            bool coversWhole = r[0] == 0 && r[2] + r[3] == pdf.Length;
            string? signer = null;
            bool valid = false;
            try
            {
                byte[] data = SignedBytes(pdf, r);
                var cms = new SignedCms(new ContentInfo(data), detached: true);
                cms.Decode(TrimSignature(contents.Bytes));
                cms.CheckSignature(verifySignatureOnly: true);
                valid = true;
                signer = cms.SignerInfos[0].Certificate?.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException or InvalidOperationException)
            {
                // An integrity failure is the finding; the signer is still named when the CMS parses.
                try
                {
                    var cms = new SignedCms();
                    cms.Decode(TrimSignature(contents.Bytes));
                    signer = cms.SignerInfos.Count > 0 ? cms.SignerInfos[0].Certificate?.GetNameInfo(X509NameType.SimpleName, false) : null;
                }
                catch (CryptographicException)
                {
                    // certificate subject parsing is best-effort
                }
            }
            found.Add((new SignatureInfo(field.Name, signer, valid, coversWhole), r[2] + r[3]));
        }
        // In revision order: the signature covering the least of the file was applied first.
        return found.OrderBy(f => f.Covered).Select(f => f.Info).ToList();
    }

    private static byte[] SignedBytes(byte[] pdf, long[] r)
    {
        if (r[0] < 0 || r[1] < 0 || r[2] < 0 || r[3] < 0 || r[0] + r[1] > pdf.Length || r[2] + r[3] > pdf.Length)
            throw new ArgumentException("The signature's byte range lies outside the file.");
        var data = new byte[r[1] + r[3]];
        Array.Copy(pdf, r[0], data, 0, r[1]);
        Array.Copy(pdf, r[2], data, r[1], r[3]);
        return data;
    }

    /// <summary>Drops the zero padding after the DER-encoded signature.</summary>
    private static byte[] TrimSignature(byte[] contents)
    {
        if (contents.Length < 4 || contents[0] != 0x30) return contents;
        int length;
        int header;
        if ((contents[1] & 0x80) == 0) { length = contents[1]; header = 2; }
        else
        {
            int n = contents[1] & 0x7F;
            if (n is < 1 or > 4 || contents.Length < 2 + n) return contents;
            length = 0;
            for (int i = 0; i < n; i++) length = length << 8 | contents[2 + i];
            header = 2 + n;
        }
        long total = (long)header + length;
        return total > 0 && total <= contents.Length ? contents.AsSpan(0, (int)total).ToArray() : contents;
    }

    /// <summary>Loads the signing certificate (with its private key) and the rest of the chain.</summary>
    private static X509Certificate2 LoadPkcs12(byte[] pkcs12, string password, out X509Certificate2Collection chain)
    {
        // Ephemeral keys never touch a key store; macOS cannot load them, so it uses its default.
        var flags = OperatingSystem.IsMacOS() ? X509KeyStorageFlags.Exportable
            : X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable;
        chain = new X509Certificate2Collection();
        chain.Import(pkcs12, password, flags);
        var withKey = chain.Cast<X509Certificate2>().FirstOrDefault(c => c.HasPrivateKey);
        if (withKey == null)
            throw new ArgumentException("The PKCS#12 file contains no private key entry.");
        return withKey;
    }
}
