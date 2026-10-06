using System.Globalization;
using System.Text;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;
using PdfEditor.Core.Pdf;
using PdfEditor.Core.Pdf.Fonts;
using CmsContentInfo = Org.BouncyCastle.Asn1.Cms.ContentInfo;

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
        var identity = LoadPkcs12(pkcs12, pkcs12Password);
        string subjectName = SimpleName(identity.Certificate) ?? "Unknown signer";

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
        return Seal(output, contentsPlaceholder, byteRangePlaceholder, identity);
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
    private static byte[] Seal(byte[] output, string contentsPlaceholder, string byteRangePlaceholder, SigningIdentity identity)
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

        // Detached CMS over SHA-256, the signer named by issuer and serial number; the signed
        // attributes carry the content type, the digest and the signing time.
        var generator = new CmsSignedDataGenerator();
        generator.AddSigner(identity.PrivateKey, identity.Certificate, CmsSignedGenerator.DigestSha256);
        generator.AddCertificates(CollectionUtilities.CreateStore(identity.Chain));
        byte[] encoded = generator.Generate(new CmsProcessableByteArray(signed), encapsulate: false).GetEncoded(Asn1Encodable.Der);
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
            var (valid, signer) = Verify(pdf, r, contents.Bytes);
            found.Add((new SignatureInfo(field.Name, signer, valid, coversWhole), r[2] + r[3]));
        }
        // In revision order: the signature covering the least of the file was applied first.
        return found.OrderBy(f => f.Covered).Select(f => f.Info).ToList();
    }

    /// <summary>
    /// Whether every signer in the CMS signed the byte range as it is, and the first signer's name.
    /// Only integrity is judged: the certificate is not checked for trust, expiry or revocation.
    /// </summary>
    private static (bool Valid, string? Signer) Verify(byte[] pdf, long[] range, byte[] contents)
    {
        CmsSignedData cms;
        try
        {
            cms = new CmsSignedData(ReadContentInfo(contents));
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            return (false, null);
        }

        var signers = cms.GetSignerInfos().GetSigners();
        var certificates = cms.GetCertificates();
        string? name = signers.Select(s => certificates.EnumerateMatches(s.SignerID).FirstOrDefault())
            .Select(c => c == null ? null : SimpleName(c)).FirstOrDefault();
        try
        {
            var signed = new CmsSignedData(new CmsProcessableByteArray(SignedBytes(pdf, range)), cms.ContentInfo);
            bool valid = signers.Count > 0 && signed.GetSignerInfos().GetSigners().All(s =>
                certificates.EnumerateMatches(s.SignerID).FirstOrDefault() is { } certificate
                && s.Verify(certificate.GetPublicKey()));
            return (valid, name);
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            // An integrity failure is the finding; the signer is still named when the CMS parses.
            return (false, name);
        }
    }

    /// <summary>The ways BouncyCastle reports a signature or certificate it cannot read or check.</summary>
    private static bool IsUnreadable(Exception ex) =>
        ex is CmsException or IOException or ArgumentException or InvalidCastException or InvalidOperationException
            or CryptoException or SecurityUtilityException;

    /// <summary>
    /// The CMS ContentInfo at the start of /Contents, which is zero-padded to the space reserved
    /// for it: only the first DER (or BER) object is read and the padding after it is ignored.
    /// </summary>
    private static CmsContentInfo ReadContentInfo(byte[] contents)
    {
        using var input = new Asn1InputStream(contents);
        return CmsContentInfo.GetInstance(input.ReadObject() ?? throw new IOException("The signature is empty."));
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

    /// <summary>The signing key, its certificate, and every certificate the PKCS#12 file holds (the signer's first).</summary>
    private sealed record SigningIdentity(AsymmetricKeyParameter PrivateKey, X509Certificate Certificate, IReadOnlyList<X509Certificate> Chain);

    /// <summary>
    /// Loads the signing key and its certificate, and the rest of the chain, from a PKCS#12 file. The
    /// key is read in memory and never reaches a platform key store.
    /// </summary>
    private static SigningIdentity LoadPkcs12(byte[] pkcs12, string password)
    {
        var contents = Pkcs12File.Read(pkcs12, password);
        if (contents.Keys.Count == 0)
            throw new ArgumentException("The PKCS#12 file contains no private key entry.");
        var (key, id) = contents.Keys[0];
        // The certificate the file pairs with the key, or failing that the one whose public key matches it.
        var certificate = contents.Certificates.FirstOrDefault(c => id != null && c.Id != null && c.Id.AsSpan().SequenceEqual(id)).Certificate
            ?? contents.Certificates.FirstOrDefault(c => IsKeyPair(key, c.Certificate.GetPublicKey())).Certificate
            ?? throw new ArgumentException("The PKCS#12 file has no certificate for its private key.");
        var chain = contents.Certificates.Select(c => c.Certificate).Where(c => !c.Equals(certificate)).Prepend(certificate).ToList();
        return new SigningIdentity(key, certificate, chain);
    }

    private static bool IsKeyPair(AsymmetricKeyParameter privateKey, AsymmetricKeyParameter publicKey) => (privateKey, publicKey) switch
    {
        (RsaKeyParameters rsa, RsaKeyParameters pub) => rsa.Modulus.Equals(pub.Modulus),
        (ECPrivateKeyParameters ec, ECPublicKeyParameters pub) => pub.Q.Equals(pub.Parameters.G.Multiply(ec.D)),
        _ => false,
    };

    /// <summary>
    /// The name to show for a certificate's subject: its common name, or failing that its
    /// organisational unit, organisation or email address; null when it has none of them.
    /// </summary>
    private static string? SimpleName(X509Certificate certificate)
    {
        var subject = certificate.SubjectDN;
        return new[] { X509Name.CN, X509Name.OU, X509Name.O, X509Name.EmailAddress }
            .Select(oid => subject.GetValueList(oid).LastOrDefault(v => !string.IsNullOrWhiteSpace(v)))
            .FirstOrDefault(v => v != null);
    }
}
