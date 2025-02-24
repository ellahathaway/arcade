// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Linq;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Pkcs;

namespace Microsoft.SignCheck.Verification
{
    public static class AuthentiCode
    {
        // https://learn.microsoft.com/en-us/windows/win32/api/wincrypt/ns-wincrypt-crypt_algorithm_identifier
        private const string OidRsaCounterSign = "1.2.840.113549.1.9.6";
        private const string OidRsaSigningTime = "1.2.840.113549.1.9.5";
        private const string OidRfc3161CounterSign = "1.3.6.1.4.1.311.3.3.1";
        private const string OidNestedSignature = "1.2.840.113549.1.9.16.2.47";
        private const string OidTimestampToken = "1.2.840.113549.1.9.16.1.4";

        public static bool IsSigned(string path)
        {
            try
            {
                if (X509Certificate2.GetCertContentType(path) == X509ContentType.Authenticode)
                {
    #pragma warning disable SYSLIB0057
                    return true;
    #pragma warning restore SYSLIB0057
                }
                else
                {
                    return false;
                }
            }
            catch (CryptographicException)
            {
                return false;
            }
        }

        private static IEnumerable<Timestamp> GetTimestampsFromCounterSignature(AsnEncodedData unsignedAttribute)
        {
            var timestamps = new List<Timestamp>();
            var rfc3161CounterSignature = new Pkcs9AttributeObject(unsignedAttribute);
            SignedCms rfc3161Message = new SignedCms();
            rfc3161Message.Decode(rfc3161CounterSignature.RawData);

            foreach (SignerInfo rfc3161SignerInfo in rfc3161Message.SignerInfos)
            {
                if (String.Equals(rfc3161Message.ContentInfo.ContentType.Value, OidTimestampToken, StringComparison.OrdinalIgnoreCase))
                {
                    var timestampToken = NuGet.Packaging.Signing.TstInfo.Read(rfc3161Message.ContentInfo.Content);

                    var timeStamp = new Timestamp
                    {
                        SignedOn = timestampToken.GenTime.LocalDateTime,
                        EffectiveDate = Convert.ToDateTime(rfc3161SignerInfo.Certificate.GetEffectiveDateString()).ToLocalTime(),
                        ExpiryDate = Convert.ToDateTime(rfc3161SignerInfo.Certificate.GetExpirationDateString()).ToLocalTime(),
                        SignatureAlgorithm = rfc3161SignerInfo.Certificate.SignatureAlgorithm.FriendlyName
                    };

                    timestamps.Add(timeStamp);
                }
            }

            return timestamps;
        }

        public static IEnumerable<Timestamp> GetTimestamps(string path)
        {
            if (String.IsNullOrEmpty(path))
            {
                return Enumerable.Empty<Timestamp>();
            }

            var timestamps = new List<Timestamp>();
            byte[] rawData = File.ReadAllBytes(path);
            var signedCms = new SignedCms();
            signedCms.Decode(rawData);

            // Timestamp information can be stored in multiple sections.
            // A single SHA1 stores the timestamp as a counter sign in the unsigned attributes
            // Multiple authenticode signatures will store additional information as a nested signature
            // In the case of SHA2 signatures, we need to find and decode the timestamp token (RFC3161).
            // Luckily NuGet implemented a proper TST and DER parser to decode this
            foreach (SignerInfo signerInfo in signedCms.SignerInfos)
            {
                foreach (CryptographicAttributeObject unsignedAttribute in signerInfo.UnsignedAttributes)
                {
                    if (String.Equals(unsignedAttribute.Oid.Value, OidRsaCounterSign, StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (SignerInfo counterSign in signerInfo.CounterSignerInfos)
                        {
                            foreach (CryptographicAttributeObject signedAttribute in counterSign.SignedAttributes)
                            {
                                if (String.Equals(signedAttribute.Oid.Value, OidRsaSigningTime, StringComparison.OrdinalIgnoreCase))
                                {
                                    var st = (Pkcs9SigningTime)signedAttribute.Values[0];
                                    X509Certificate2 cert = counterSign.Certificate;

                                    var timeStamp = new Timestamp
                                    {
                                        SignedOn = st.SigningTime.ToLocalTime(),
                                        EffectiveDate = Convert.ToDateTime(cert.GetEffectiveDateString()).ToLocalTime(),
                                        ExpiryDate = Convert.ToDateTime(cert.GetExpirationDateString()).ToLocalTime(),
                                        SignatureAlgorithm = cert.SignatureAlgorithm.FriendlyName
                                    };

                                    timestamps.Add(timeStamp);
                                }
                            }
                        }
                    }
                    else if (String.Equals(unsignedAttribute.Oid.Value, OidRfc3161CounterSign, StringComparison.OrdinalIgnoreCase))
                    {
                        timestamps.AddRange(GetTimestampsFromCounterSignature(unsignedAttribute.Values[0]));
                    }
                    else if (String.Equals(unsignedAttribute.Oid.Value, OidNestedSignature, StringComparison.OrdinalIgnoreCase))
                    {
                        var nestedSignature = new Pkcs9AttributeObject(unsignedAttribute.Values[0]);
                        SignedCms nestedSignatureMessage = new SignedCms();
                        nestedSignatureMessage.Decode(nestedSignature.RawData);

                        foreach (SignerInfo nestedSignerInfo in nestedSignatureMessage.SignerInfos)
                        {
                            foreach (CryptographicAttributeObject nestedUnsignedAttribute in nestedSignerInfo.UnsignedAttributes)
                            {
                                if (String.Equals(nestedUnsignedAttribute.Oid.Value, OidRfc3161CounterSign, StringComparison.OrdinalIgnoreCase))
                                {
                                    timestamps.AddRange(GetTimestampsFromCounterSignature(nestedUnsignedAttribute.Values[0]));
                                }
                            }
                        }
                    }
                }
            }

            return timestamps;
        }
    }
}
