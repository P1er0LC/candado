// Primitivas de cifrado de Candado: AES-256-CBC con HMAC-SHA256 (cifrar y después
// autenticar) y PBKDF2-SHA256 para las contraseñas. AES es el de .NET Framework; HMAC y
// PBKDF2 son los de Windows (CNG), porque los de .NET Framework son código administrado:
// PBKDF2 tardaba 9 s por intento de contraseña y HMAC no pasaba de 160 MB/s.
// Una «llave» son siempre 64 bytes: los primeros 32 cifran y los otros 32 autentican.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Candado
{
    static class CNG
    {
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        static extern int BCryptOpenAlgorithmProvider(out IntPtr algoritmo, string id, string implementacion, int banderas);
        [DllImport("bcrypt.dll")]
        static extern int BCryptDeriveKeyPBKDF2(IntPtr prf, byte[] clave, int largoClave, byte[] sal, int largoSal, ulong iteraciones, byte[] salida, int largoSalida, int banderas);
        [DllImport("bcrypt.dll")]
        static extern int BCryptCreateHash(IntPtr algoritmo, out IntPtr hash, IntPtr objeto, int largoObjeto, byte[] secreto, int largoSecreto, int banderas);
        [DllImport("bcrypt.dll")]
        static extern unsafe int BCryptHashData(IntPtr hash, byte* datos, int largo, int banderas);
        [DllImport("bcrypt.dll")]
        static extern int BCryptFinishHash(IntPtr hash, byte[] salida, int largo, int banderas);
        [DllImport("bcrypt.dll")]
        static extern int BCryptDestroyHash(IntPtr hash);

        const int HmacBandera = 0x8;   // BCRYPT_ALG_HANDLE_HMAC_FLAG
        static readonly IntPtr Sha256Hmac = Abrir();

        static IntPtr Abrir()
        {
            IntPtr a;
            Revisar(BCryptOpenAlgorithmProvider(out a, "SHA256", null, HmacBandera));
            return a;
        }

        static void Revisar(int estado)
        {
            if (estado != 0) throw new CryptographicException("CNG devolvió 0x" + estado.ToString("X8"));
        }

        public static byte[] Pbkdf2(byte[] clave, byte[] sal, int iteraciones, int largo)
        {
            byte[] r = new byte[largo];
            Revisar(BCryptDeriveKeyPBKDF2(Sha256Hmac, clave, clave.Length, sal, sal.Length, (ulong)iteraciones, r, largo, 0));
            return r;
        }

        public sealed class Hmac : IDisposable
        {
            IntPtr h;
            public Hmac(byte[] llave) { Revisar(BCryptCreateHash(Sha256Hmac, out h, IntPtr.Zero, 0, llave, llave.Length, 0)); }

            public unsafe void Agregar(byte[] b, int desde, int n)
            {
                if (n <= 0) return;
                fixed (byte* p = b) Revisar(BCryptHashData(h, p + desde, n, 0));
            }

            public byte[] Terminar()
            {
                byte[] r = new byte[32];
                Revisar(BCryptFinishHash(h, r, 32, 0));
                return r;
            }

            public void Dispose()
            {
                if (h != IntPtr.Zero) { BCryptDestroyHash(h); h = IntPtr.Zero; }
            }
        }
    }

    public class ErrorCandado : Exception
    {
        public ErrorCandado(string mensaje) : base(mensaje) { }
    }

    public static class Cripto
    {
        // OWASP pide 600.000, pero en esta laptop eso son 2-3 s por intento. Cada carpeta guarda
        // las suyas en la cabecera, así que subirlas después no rompe nada.
        public const int IteracionesContrasena = 310000;
        public const int IteracionesRecuperacion = 100000;  // la clave ya tiene 160 bits de azar
        const int Bufer = 4 << 20;   // con 4 MB el disco mecánico salta menos entre leer y escribir
        static readonly byte[] Magia = Encoding.ASCII.GetBytes("CND1");
        static readonly RandomNumberGenerator Generador = RandomNumberGenerator.Create();
        const string Base32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        public static byte[] Azar(int n)
        {
            byte[] b = new byte[n];
            Generador.GetBytes(b);
            return b;
        }

        public static string Hex(byte[] b)
        {
            StringBuilder s = new StringBuilder(b.Length * 2);
            foreach (byte x in b) s.Append(x.ToString("x2"));
            return s.ToString();
        }

        public static byte[] Derivar(string secreto, byte[] sal, int iteraciones)
        {
            return CNG.Pbkdf2(Encoding.UTF8.GetBytes(secreto), sal, iteraciones, 64);
        }

        public static byte[] Hmac(byte[] llave, byte[] datos, int desde, int largo)
        {
            using (CNG.Hmac h = new CNG.Hmac(llave)) { h.Agregar(datos, desde, largo); return h.Terminar(); }
        }

        // 20 bytes de azar en base32, en grupos de 4: «7K4M-Q2XP-…».
        public static string NuevaClaveRecuperacion()
        {
            byte[] b = Azar(20);
            StringBuilder s = new StringBuilder();
            int bits = 0, valor = 0;
            foreach (byte x in b)
            {
                valor = (valor << 8) | x; bits += 8;
                while (bits >= 5) { s.Append(Base32[(valor >> (bits - 5)) & 31]); bits -= 5; }
            }
            StringBuilder g = new StringBuilder();
            for (int i = 0; i < s.Length; i++) { if (i > 0 && i % 4 == 0) g.Append('-'); g.Append(s[i]); }
            return g.ToString();
        }

        // Acepta la clave con minúsculas, espacios o sin guiones, y confunde a propósito
        // los caracteres que se parecen (0/O, 1/I, 8/B), que base32 no usa.
        public static string NormalizarClave(string clave)
        {
            StringBuilder s = new StringBuilder();
            foreach (char c in (clave ?? "").ToUpperInvariant())
            {
                char x = c == '0' ? 'O' : c == '1' ? 'I' : c == '8' ? 'B' : c;
                if (Base32.IndexOf(x) >= 0) s.Append(x);
            }
            return s.ToString();
        }

        static Aes NuevoAes()
        {
            Aes aes = Aes.Create();
            aes.KeySize = 256; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            return aes;
        }

        static byte[] Parte(byte[] b, int desde, int largo)
        {
            byte[] r = new byte[largo];
            Buffer.BlockCopy(b, desde, r, 0, largo);
            return r;
        }

        static bool Iguales(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int d = 0;
            for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }

        // Para datos chicos (llaves e índice): IV | cifrado | HMAC.
        public static byte[] Cerrar(byte[] llave, byte[] datos)
        {
            byte[] iv = Azar(16);
            byte[] cifrado;
            using (Aes aes = NuevoAes())
            using (ICryptoTransform t = aes.CreateEncryptor(Parte(llave, 0, 32), iv))
                cifrado = t.TransformFinalBlock(datos, 0, datos.Length);
            byte[] cuerpo = new byte[16 + cifrado.Length];
            Buffer.BlockCopy(iv, 0, cuerpo, 0, 16);
            Buffer.BlockCopy(cifrado, 0, cuerpo, 16, cifrado.Length);
            byte[] etiqueta = Hmac(Parte(llave, 32, 32), cuerpo, 0, cuerpo.Length);
            byte[] r = new byte[cuerpo.Length + 32];
            Buffer.BlockCopy(cuerpo, 0, r, 0, cuerpo.Length);
            Buffer.BlockCopy(etiqueta, 0, r, cuerpo.Length, 32);
            return r;
        }

        // Devuelve null si la llave no corresponde o los datos se alteraron.
        public static byte[] Abrir(byte[] llave, byte[] cerrado)
        {
            if (cerrado == null || cerrado.Length < 16 + 16 + 32 || (cerrado.Length - 48) % 16 != 0) return null;
            int largoCuerpo = cerrado.Length - 32;
            byte[] esperada = Hmac(Parte(llave, 32, 32), cerrado, 0, largoCuerpo);
            if (!Iguales(esperada, Parte(cerrado, largoCuerpo, 32))) return null;
            using (Aes aes = NuevoAes())
            using (ICryptoTransform t = aes.CreateDecryptor(Parte(llave, 0, 32), Parte(cerrado, 0, 16)))
                return t.TransformFinalBlock(cerrado, 16, largoCuerpo - 16);
        }

        static void LeerJusto(Stream s, byte[] b, int n)
        {
            int leido = 0;
            while (leido < n)
            {
                int r = s.Read(b, leido, n - leido);
                if (r <= 0) throw new EndOfStreamException();
                leido += r;
            }
        }

        // Archivo cifrado: «CND1» | IV | cifrado | HMAC de todo lo anterior. El destino no
        // debe existir; si algo falla, lo borra quien llama.
        public static void CifrarArchivo(byte[] llave, string origen, string destino, Action<long> avance, CancellationToken cancelar)
        {
            using (FileStream entrada = new FileStream(origen, FileMode.Open, FileAccess.Read, FileShare.Read, Bufer, FileOptions.SequentialScan))
            using (FileStream salida = new FileStream(destino, FileMode.CreateNew, FileAccess.Write, FileShare.None, Bufer))
            using (CNG.Hmac h = new CNG.Hmac(Parte(llave, 32, 32)))
            using (Aes aes = NuevoAes())
            {
                byte[] iv = Azar(16);
                salida.Write(Magia, 0, 4); salida.Write(iv, 0, 16);
                h.Agregar(Magia, 0, 4); h.Agregar(iv, 0, 16);
                byte[] bufer = new byte[Bufer];
                byte[] cifrado = new byte[Bufer + 32];
                using (ICryptoTransform t = aes.CreateEncryptor(Parte(llave, 0, 32), iv))
                {
                    long restante = entrada.Length;
                    while (restante > Bufer)
                    {
                        cancelar.ThrowIfCancellationRequested();
                        LeerJusto(entrada, bufer, Bufer);
                        int n = t.TransformBlock(bufer, 0, Bufer, cifrado, 0);
                        salida.Write(cifrado, 0, n); h.Agregar(cifrado, 0, n);
                        restante -= Bufer;
                        if (avance != null) avance(Bufer);
                    }
                    int ultimo = (int)restante;
                    LeerJusto(entrada, bufer, ultimo);
                    byte[] fin = t.TransformFinalBlock(bufer, 0, ultimo);
                    salida.Write(fin, 0, fin.Length); h.Agregar(fin, 0, fin.Length);
                    if (avance != null) avance(ultimo);
                }
                salida.Write(h.Terminar(), 0, 32);
                salida.Flush(true);
            }
        }

        // Descifra comprobando el HMAC antes de escribir el último bloque; si no coincide,
        // tira ErrorCandado (el destino a medias lo borra quien llama).
        public static void DescifrarArchivo(byte[] llave, string origen, string destino, Action<long> avance, CancellationToken cancelar)
        {
            using (FileStream entrada = new FileStream(origen, FileMode.Open, FileAccess.Read, FileShare.Read, Bufer, FileOptions.SequentialScan))
            {
                long cuerpo = entrada.Length - 20 - 32;
                if (cuerpo < 16 || cuerpo % 16 != 0) throw new ErrorCandado("dañado");
                byte[] cabecera = new byte[20];
                LeerJusto(entrada, cabecera, 20);
                for (int i = 0; i < 4; i++) if (cabecera[i] != Magia[i]) throw new ErrorCandado("dañado");
                using (FileStream salida = new FileStream(destino, FileMode.CreateNew, FileAccess.Write, FileShare.None, Bufer))
                using (CNG.Hmac h = new CNG.Hmac(Parte(llave, 32, 32)))
                using (Aes aes = NuevoAes())
                using (ICryptoTransform t = aes.CreateDecryptor(Parte(llave, 0, 32), Parte(cabecera, 4, 16)))
                {
                    h.Agregar(cabecera, 0, 20);
                    byte[] bufer = new byte[Bufer];
                    byte[] claro = new byte[Bufer + 32];
                    long restante = cuerpo;
                    while (restante > 0)
                    {
                        cancelar.ThrowIfCancellationRequested();
                        int n = (int)Math.Min(Bufer, restante);
                        LeerJusto(entrada, bufer, n);
                        h.Agregar(bufer, 0, n);
                        int m = t.TransformBlock(bufer, 0, n, claro, 0);
                        salida.Write(claro, 0, m);
                        restante -= n;
                        if (avance != null) avance(n);
                    }
                    byte[] etiqueta = new byte[32];
                    LeerJusto(entrada, etiqueta, 32);
                    if (!Iguales(h.Terminar(), etiqueta)) throw new ErrorCandado("dañado");
                    byte[] fin = t.TransformFinalBlock(new byte[0], 0, 0);
                    salida.Write(fin, 0, fin.Length);
                    salida.Flush(true);
                }
            }
        }

        // Cierra con la cuenta de Windows (DPAPI).
        static readonly byte[] Entropia = Encoding.UTF8.GetBytes("Candado.v1");
        public static string CerrarConWindows(byte[] datos)
        {
            return Convert.ToBase64String(ProtectedData.Protect(datos, Entropia, DataProtectionScope.CurrentUser));
        }

        public static byte[] AbrirConWindows(string cerrado)
        {
            if (string.IsNullOrEmpty(cerrado)) return null;
            try { return ProtectedData.Unprotect(Convert.FromBase64String(cerrado), Entropia, DataProtectionScope.CurrentUser); }
            catch (CryptographicException) { return null; }
        }
    }
}
