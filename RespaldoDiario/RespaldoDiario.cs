// Respaldo Diario
//  - Respaldo: comprime los archivos de una carpeta en "Respaldo AAAA-MM-DD.rar" y los mueve a una carpeta de respaldo.
//  - Depuración: cada mes borra los respaldos viejos y de cada mes conserva solo los últimos N días.
// Ambas cosas se programan en el Programador de tareas de Windows.
//
//   RespaldoDiario.exe            -> abre la ventana de configuración
//   RespaldoDiario.exe /auto      -> ejecuta el respaldo sin ventana (tarea diaria)
//   RespaldoDiario.exe /depurar   -> ejecuta la depuración sin ventana (tarea mensual)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Respaldo Diario")]
[assembly: System.Reflection.AssemblyProduct("Respaldo Diario")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.0.0")]

namespace RespaldoDiario
{
    // ------------------------------------------------------------------ Configuración
    class Config
    {
        // Respaldo
        public string Origen = "";
        public string Destino = "";
        public string Filtro = "*";
        public string Prefijo = "Respaldo";
        public string Formato = "RAR";          // RAR o ZIP
        public string RarPath = "";
        public bool DiaAnterior = true;         // a la 1 AM los archivos son del día anterior
        public string Hora = "01:00";

        // Depuración
        public List<string> DepCarpetas = new List<string>();
        public string DepFiltro = "*";
        public int DepDiasFinMes = 5;           // conservar los últimos N días de cada mes
        public int DepMinRecientes = 5;         // conservar siempre los N más recientes de cada carpeta
        public bool DepUsarFechaMod = false;    // si el nombre no tiene fecha, usar la fecha de modificación
        public int DepDia = 1;                  // día del mes en que se ejecuta
        public string DepHora = "02:00";

        public static string Carpeta
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.CommonApplicationData), "RespaldoDiario");
            }
        }
        static string Archivo { get { return Path.Combine(Carpeta, "config.ini"); } }

        static int Entero(string v, int def, int min, int max)
        {
            int n; return int.TryParse(v, out n) ? Math.Max(min, Math.Min(max, n)) : def;
        }

        public static Config Cargar()
        {
            var c = new Config();
            if (File.Exists(Archivo))
            {
                foreach (var linea in File.ReadAllLines(Archivo, Encoding.UTF8))
                {
                    int i = linea.IndexOf('=');
                    if (i <= 0) continue;
                    string k = linea.Substring(0, i).Trim(), v = linea.Substring(i + 1).Trim();
                    switch (k)
                    {
                        case "Origen": c.Origen = v; break;
                        case "Destino": c.Destino = v; break;
                        case "Filtro": c.Filtro = v; break;
                        case "Prefijo": c.Prefijo = v; break;
                        case "Formato": c.Formato = v; break;
                        case "RarPath": c.RarPath = v; break;
                        case "DiaAnterior": c.DiaAnterior = v == "1"; break;
                        case "Hora": c.Hora = v; break;
                        case "DepCarpetas": c.DepCarpetas = v.Split('|').Where(s => s.Trim() != "").ToList(); break;
                        case "DepFiltro": c.DepFiltro = v; break;
                        case "DepDiasFinMes": c.DepDiasFinMes = Entero(v, 5, 1, 28); break;
                        case "DepMinRecientes": c.DepMinRecientes = Entero(v, 5, 0, 365); break;
                        case "DepUsarFechaMod": c.DepUsarFechaMod = v == "1"; break;
                        case "DepDia": c.DepDia = Entero(v, 1, 1, 28); break;
                        case "DepHora": c.DepHora = v; break;
                    }
                }
            }
            if (string.IsNullOrEmpty(c.RarPath)) c.RarPath = Respaldo.BuscarRar();
            return c;
        }

        public void Guardar()
        {
            Directory.CreateDirectory(Carpeta);
            File.WriteAllLines(Archivo, new[] {
                "Origen=" + Origen, "Destino=" + Destino, "Filtro=" + Filtro,
                "Prefijo=" + Prefijo, "Formato=" + Formato, "RarPath=" + RarPath,
                "DiaAnterior=" + (DiaAnterior ? "1" : "0"), "Hora=" + Hora,
                "DepCarpetas=" + string.Join("|", DepCarpetas), "DepFiltro=" + DepFiltro,
                "DepDiasFinMes=" + DepDiasFinMes, "DepMinRecientes=" + DepMinRecientes,
                "DepUsarFechaMod=" + (DepUsarFechaMod ? "1" : "0"),
                "DepDia=" + DepDia, "DepHora=" + DepHora
            }, Encoding.UTF8);
        }
    }

    // ------------------------------------------------------------------ Utilidades
    static class Util
    {
        public static string LogPath { get { return Path.Combine(Config.Carpeta, "respaldo.log"); } }

        public static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(Config.Carpeta);
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        public static string Tamano(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.00") + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.0") + " MB";
            return (bytes / 1024.0).ToString("0") + " KB";
        }

        /// Archivos de la carpeta que coinciden con uno o varios filtros separados por ";" (ej. "*.rar;*.zip").
        public static List<FileInfo> Archivos(string carpeta, string filtros)
        {
            var dir = new DirectoryInfo(carpeta);
            var pats = (filtros ?? "").Split(';').Select(s => s.Trim()).Where(s => s != "").ToList();
            if (pats.Count == 0) pats.Add("*");
            return pats.SelectMany(p => dir.GetFiles(p))
                       .GroupBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                       .Select(g => g.First()).ToList();
        }
    }

    // ------------------------------------------------------------------ Respaldo diario
    static class Respaldo
    {
        public static string BuscarRar()
        {
            foreach (var pf in new[] { Environment.GetEnvironmentVariable("ProgramW6432"),
                                       Environment.GetEnvironmentVariable("ProgramFiles"),
                                       Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
            {
                if (string.IsNullOrEmpty(pf)) continue;
                string p = Path.Combine(pf, @"WinRAR\Rar.exe");
                if (File.Exists(p)) return p;
            }
            return "";
        }

        public static List<FileInfo> ArchivosPendientes(Config c)
        {
            return Util.Archivos(c.Origen, c.Filtro)
                       .Where(f => !f.Name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) &&
                                   !f.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                       .ToList();
        }

        static bool EstaLibre(string ruta)
        {
            try { using (File.Open(ruta, FileMode.Open, FileAccess.Read, FileShare.None)) { } return true; }
            catch { return false; }
        }

        static int Ejecutar(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true };
            using (var p = Process.Start(psi)) { p.WaitForExit(); return p.ExitCode; }
        }

        static string Q(string s) { return "\"" + s + "\""; }

        public static string Ejecutar(Config c)
        {
            using (var mutex = new Mutex(false, @"Global\RespaldoDiarioMutex"))
            {
                if (!mutex.WaitOne(0)) return "Ya hay un respaldo en curso.";
                try { return EjecutarInterno(c); }
                finally { mutex.ReleaseMutex(); }
            }
        }

        static string EjecutarInterno(Config c)
        {
            if (!Directory.Exists(c.Origen)) throw new Exception("No existe la carpeta de origen: " + c.Origen);
            Directory.CreateDirectory(c.Destino);

            var archivos = ArchivosPendientes(c);
            if (archivos.Count == 0) return "No hay archivos en la carpeta de origen. Nada que respaldar.";

            // Esperar a que terminen de escribirse (tamaño estable y sin bloqueo), hasta 10 minutos
            for (int intento = 0; ; intento++)
            {
                var tam = archivos.ToDictionary(f => f.FullName, f => f.Length);
                Thread.Sleep(10000);
                bool listos = archivos.All(f => { f.Refresh(); return f.Exists && f.Length == tam[f.FullName] && EstaLibre(f.FullName); });
                if (listos) break;
                if (intento >= 60) throw new Exception("Los archivos siguen en uso después de 10 minutos.");
                archivos = ArchivosPendientes(c);
            }

            bool rar = c.Formato != "ZIP";
            if (rar && !File.Exists(c.RarPath)) throw new Exception("No se encontró Rar.exe en: " + c.RarPath);
            string ext = rar ? ".rar" : ".zip";

            DateTime fecha = c.DiaAnterior ? DateTime.Today.AddDays(-1) : DateTime.Today;
            string baseNombre = (c.Prefijo + " " + fecha.ToString("yyyy-MM-dd")).Trim();
            string nombre = baseNombre + ext;
            for (int n = 2; File.Exists(Path.Combine(c.Destino, nombre)); n++)
                nombre = baseNombre + " (" + n + ")" + ext;

            string temp = Path.Combine(c.Origen, nombre + ".tmp" + ext);
            if (File.Exists(temp)) File.Delete(temp);

            if (rar)
            {
                var sb = new StringBuilder("a -ep1 -m5 -y -idq -- " + Q(temp));
                foreach (var f in archivos) sb.Append(' ').Append(Q(f.FullName));
                int code = Ejecutar(c.RarPath, sb.ToString());
                if (code != 0) throw new Exception("WinRAR falló al comprimir (código " + code + ").");
                code = Ejecutar(c.RarPath, "t -idq " + Q(temp));
                if (code != 0) throw new Exception("El .rar no pasó la verificación (código " + code + ").");
            }
            else
            {
                using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
                    foreach (var f in archivos)
                        zip.CreateEntryFromFile(f.FullName, f.Name, CompressionLevel.Optimal);
                using (var zip = ZipFile.OpenRead(temp))
                    if (zip.Entries.Count != archivos.Count) throw new Exception("El .zip no pasó la verificación.");
            }

            string final = Path.Combine(c.Destino, nombre);
            File.Move(temp, final);                       // mover al respaldo
            foreach (var f in archivos) f.Delete();       // "cortar": borrar originales

            return "OK  " + nombre + "  (" + archivos.Count + " archivos: " + string.Join(", ", archivos.Select(f => f.Name)) + ")";
        }
    }

    // ------------------------------------------------------------------ Depuración mensual
    class Candidato
    {
        public FileInfo Archivo;
        public DateTime Fecha;
        public bool FechaDeNombre;
    }

    class ResultadoDepuracion
    {
        public List<Candidato> Borrar = new List<Candidato>();
        public int Conservados;
        public int SinFecha;
        public List<string> Avisos = new List<string>();
        public long Bytes { get { return Borrar.Sum(b => b.Archivo.Length); } }
    }

    static class Depuracion
    {
        static readonly Regex AMD = new Regex(@"(?<!\d)(\d{4})[-_. ]?(\d{2})[-_. ]?(\d{2})(?!\d)");   // 2026-11-09, 20261109
        static readonly Regex DMA = new Regex(@"(?<!\d)(\d{2})[-_. ](\d{2})[-_. ](\d{4})(?!\d)");    // 09-11-2026

        static bool Fecha(int a, int m, int d, out DateTime f)
        {
            f = DateTime.MinValue;
            if (a < 2000 || a > 2099 || m < 1 || m > 12 || d < 1 || d > DateTime.DaysInMonth(a, m)) return false;
            f = new DateTime(a, m, d); return true;
        }

        public static bool FechaDelNombre(string nombre, out DateTime f)
        {
            string s = Path.GetFileNameWithoutExtension(nombre);
            foreach (Match m in AMD.Matches(s))
                if (Fecha(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), out f)) return true;
            foreach (Match m in DMA.Matches(s))
                if (Fecha(int.Parse(m.Groups[3].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[1].Value), out f)) return true;
            f = DateTime.MinValue; return false;
        }

        /// Calcula qué se borraría. No borra nada.
        public static ResultadoDepuracion Analizar(Config c)
        {
            var r = new ResultadoDepuracion();
            DateTime inicioMesActual = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);

            foreach (var carpeta in c.DepCarpetas)
            {
                if (!Directory.Exists(carpeta)) { r.Avisos.Add("No existe la carpeta: " + carpeta); continue; }

                var lista = new List<Candidato>();
                foreach (var f in Util.Archivos(carpeta, c.DepFiltro))
                {
                    DateTime fecha;
                    if (FechaDelNombre(f.Name, out fecha)) lista.Add(new Candidato { Archivo = f, Fecha = fecha, FechaDeNombre = true });
                    else if (c.DepUsarFechaMod) lista.Add(new Candidato { Archivo = f, Fecha = f.LastWriteTime.Date });
                    else r.SinFecha++;
                }

                // Siempre conservar los N más recientes de la carpeta
                var protegidos = new HashSet<string>(lista.OrderByDescending(x => x.Fecha).ThenByDescending(x => x.Archivo.Name)
                                                          .Take(c.DepMinRecientes).Select(x => x.Archivo.FullName));

                foreach (var x in lista)
                {
                    bool conservar =
                        x.Fecha >= inicioMesActual ||                                                  // mes en curso: no se toca
                        x.Fecha.Day > DateTime.DaysInMonth(x.Fecha.Year, x.Fecha.Month) - c.DepDiasFinMes || // últimos N días del mes
                        protegidos.Contains(x.Archivo.FullName);
                    if (conservar) r.Conservados++; else r.Borrar.Add(x);
                }
            }
            r.Borrar = r.Borrar.OrderBy(b => b.Archivo.DirectoryName).ThenBy(b => b.Fecha).ToList();
            return r;
        }

        /// Borra lo indicado. Devuelve el resumen para el registro.
        public static string Borrar(ResultadoDepuracion r)
        {
            using (var mutex = new Mutex(false, @"Global\RespaldoDiarioDepuracionMutex"))
            {
                if (!mutex.WaitOne(0)) return "Ya hay una depuración en curso.";
                try
                {
                    int ok = 0, errores = 0; long bytes = 0;
                    foreach (var b in r.Borrar)
                    {
                        try
                        {
                            long tam = b.Archivo.Length;
                            b.Archivo.Delete();
                            ok++; bytes += tam;
                            Util.Log("    borrado: " + b.Archivo.FullName);
                        }
                        catch (Exception ex) { errores++; Util.Log("    ERROR al borrar " + b.Archivo.FullName + ": " + ex.Message); }
                    }
                    return "Depuración: " + ok + " archivos borrados, " + Util.Tamano(bytes) + " liberados, " + r.Conservados + " conservados" +
                           (errores > 0 ? ", " + errores + " con error" : "") +
                           (r.SinFecha > 0 ? ", " + r.SinFecha + " sin fecha en el nombre (no se tocaron)" : "") + ".";
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }

    // ------------------------------------------------------------------ Tareas programadas
    static class Tarea
    {
        public const string Respaldo = "RespaldoDiario";
        public const string Depuracion = "RespaldoDiario-Depuracion";

        static int Schtasks(string args, out string salida)
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                salida = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        public static bool Existe(string nombre) { string s; return Schtasks("/query /tn " + nombre, out s) == 0; }

        static void Crear(string nombre, string programa, string argumento)
        {
            string exe = Application.ExecutablePath;
            string args = "/create /f /tn " + nombre + " " + programa +
                          " /ru SYSTEM /rl HIGHEST /tr \"\\\"" + exe + "\\\" " + argumento + "\"";
            string s;
            if (Schtasks(args, out s) != 0) throw new Exception(s.Trim());
        }

        public static void CrearDiaria(string hora) { Crear(Respaldo, "/sc daily /st " + hora, "/auto"); }
        public static void CrearMensual(int dia, string hora) { Crear(Depuracion, "/sc monthly /d " + dia + " /st " + hora, "/depurar"); }

        public static void Borrar(string nombre) { string s; Schtasks("/delete /f /tn " + nombre, out s); }
    }

    // ------------------------------------------------------------------ Ventana de vista previa
    class VistaPrevia : Form
    {
        public VistaPrevia(ResultadoDepuracion r, bool permitirBorrar)
        {
            Text = "Vista previa de la depuración";
            Font = new Font("Segoe UI", 9.5f);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 480);
            MinimizeBox = false;

            var lista = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
            lista.Columns.Add("Carpeta", 250); lista.Columns.Add("Archivo", 250);
            lista.Columns.Add("Fecha", 95); lista.Columns.Add("Tamaño", 90, HorizontalAlignment.Right);
            lista.BeginUpdate();
            foreach (var b in r.Borrar)
                lista.Items.Add(new ListViewItem(new[] {
                    b.Archivo.DirectoryName, b.Archivo.Name,
                    b.Fecha.ToString("yyyy-MM-dd") + (b.FechaDeNombre ? "" : " *"), Util.Tamano(b.Archivo.Length) }));
            lista.EndUpdate();

            var resumen = new Label { Dock = DockStyle.Top, Height = 64, Padding = new Padding(8, 8, 8, 0) };
            resumen.Text = (r.Borrar.Count == 0 ? "No hay nada que borrar." :
                            "Se borrarían " + r.Borrar.Count + " archivos y se liberarían " + Util.Tamano(r.Bytes) + ".") +
                           "\nSe conservan " + r.Conservados + " archivos." +
                           (r.SinFecha > 0 ? "  " + r.SinFecha + " archivos no tienen fecha en el nombre y no se tocan." : "") +
                           (r.Avisos.Count > 0 ? "\n" + string.Join("  ", r.Avisos) : "") +
                           (r.Borrar.Any(b => !b.FechaDeNombre) ? "\n* fecha tomada de la fecha de modificación" : "");

            var abajo = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            var cerrar = new Button { Text = "Cerrar", Width = 100, Height = 32, DialogResult = DialogResult.Cancel };
            abajo.Controls.Add(cerrar);
            if (permitirBorrar && r.Borrar.Count > 0)
            {
                var borrar = new Button { Text = "Borrar " + r.Borrar.Count + " archivos", Width = 180, Height = 32, ForeColor = Color.Firebrick };
                borrar.Click += delegate
                {
                    if (MessageBox.Show(this, "Se borrarán " + r.Borrar.Count + " archivos (" + Util.Tamano(r.Bytes) + ") de forma DEFINITIVA.\n¿Continuar?",
                                        "Confirmar", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                    { DialogResult = DialogResult.OK; Close(); }
                };
                abajo.Controls.Add(borrar);
            }
            CancelButton = cerrar;
            Controls.Add(lista); Controls.Add(resumen); Controls.Add(abajo);
        }
    }

    // ------------------------------------------------------------------ Ventana principal
    class Ventana : Form
    {
        // Respaldo
        TextBox txtOrigen = new TextBox(), txtDestino = new TextBox(), txtFiltro = new TextBox(),
                txtPrefijo = new TextBox(), txtRar = new TextBox();
        ComboBox cmbFormato = new ComboBox(), cmbFecha = new ComboBox();
        DateTimePicker dtpHora = new DateTimePicker();
        Label lblEstado = new Label(), lblEjemplo = new Label();
        Button btnGuardar = new Button(), btnAhora = new Button(), btnQuitar = new Button();

        // Depuración
        ListBox lstCarpetas = new ListBox();
        TextBox txtDepFiltro = new TextBox();
        NumericUpDown numDias = new NumericUpDown(), numMin = new NumericUpDown(), numDia = new NumericUpDown();
        CheckBox chkFechaMod = new CheckBox();
        DateTimePicker dtpDepHora = new DateTimePicker();
        Label lblDepEstado = new Label(), lblDepEjemplo = new Label();

        // Registro
        TextBox txtLog = new TextBox();

        public Ventana()
        {
            Text = "Respaldo Diario";
            Font = new Font("Segoe UI", 9.5f);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(680, 600);
            MinimumSize = new Size(600, 560);

            var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
            var tabResp = new TabPage("Respaldo diario");
            var tabDep = new TabPage("Depuración mensual");
            var tabLog = new TabPage("Registro");
            tabs.TabPages.AddRange(new[] { tabResp, tabDep, tabLog });
            tabs.SelectedIndexChanged += delegate { if (tabs.SelectedTab == tabLog) CargarLog(); };
            Controls.Add(tabs);

            ConstruirRespaldo(tabResp);
            ConstruirDepuracion(tabDep);

            txtLog.Multiline = true; txtLog.ReadOnly = true; txtLog.ScrollBars = ScrollBars.Both; txtLog.WordWrap = false;
            txtLog.Dock = DockStyle.Fill; txtLog.Font = new Font("Consolas", 9f); txtLog.BackColor = SystemColors.Window;
            tabLog.Padding = new Padding(8); tabLog.Controls.Add(txtLog);

            Cargar();
        }

        static TableLayoutPanel Tabla(TabPage tab)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 3, AutoScroll = true };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
            tab.Controls.Add(t);
            return t;
        }

        void ConstruirRespaldo(TabPage tab)
        {
            var t = Tabla(tab);
            Fila(t, "Carpeta de origen:", txtOrigen, Boton("Examinar...", delegate { ElegirCarpeta(txtOrigen); }));
            Fila(t, "Carpeta de respaldo:", txtDestino, Boton("Examinar...", delegate { ElegirCarpeta(txtDestino); }));

            cmbFormato.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbFormato.Items.AddRange(new object[] { "RAR (WinRAR)", "ZIP (no requiere WinRAR)" });
            cmbFormato.SelectedIndexChanged += delegate { txtRar.Enabled = cmbFormato.SelectedIndex == 0; ActualizarEjemplo(); };
            Fila(t, "Formato:", cmbFormato, null);

            Fila(t, "Ruta de Rar.exe:", txtRar, Boton("Examinar...", delegate { ElegirRar(); }));
            Fila(t, "Nombre del archivo:", txtPrefijo, null);
            txtPrefijo.TextChanged += delegate { ActualizarEjemplo(); };

            cmbFecha.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbFecha.Items.AddRange(new object[] { "Día anterior (recomendado a la 1 AM)", "Día en que se ejecuta" });
            cmbFecha.SelectedIndexChanged += delegate { ActualizarEjemplo(); };
            Fila(t, "Fecha en el nombre:", cmbFecha, null);
            lblEjemplo.ForeColor = Color.DimGray; lblEjemplo.AutoSize = true;
            Fila(t, "", lblEjemplo, null);

            Fila(t, "Archivos a incluir:", txtFiltro, null);
            Fila(t, "", Ayuda("* = todos   ·   *.csv = solo CSV   ·   *.csv;*.txt = varios"), null);

            HoraPicker(dtpHora);
            Fila(t, "Ejecutar todos los días a las:", dtpHora, null);

            ConfigBoton(btnGuardar, "Guardar y programar", 170); btnGuardar.Click += delegate { GuardarRespaldo(); };
            ConfigBoton(btnAhora, "Respaldar ahora", 140); btnAhora.Click += delegate { RespaldarAhora(); };
            ConfigBoton(btnQuitar, "Quitar programación", 160);
            btnQuitar.Click += delegate { Tarea.Borrar(Tarea.Respaldo); Util.Log("Respaldo diario: programación quitada."); ActualizarEstado(); };
            FilaBotones(t, btnGuardar, btnAhora, btnQuitar);
            FilaEstado(t, lblEstado);
        }

        void ConstruirDepuracion(TabPage tab)
        {
            var t = Tabla(tab);

            lstCarpetas.Height = 110; lstCarpetas.HorizontalScrollbar = true;
            var botonesCarpeta = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(0) };
            var agregar = new Button { Text = "Agregar...", Width = 100, Height = 28 };
            var quitar = new Button { Text = "Quitar", Width = 100, Height = 28 };
            agregar.Click += delegate
            {
                using (var d = new FolderBrowserDialog { ShowNewFolderButton = false, SelectedPath = txtDestino.Text })
                    if (d.ShowDialog(this) == DialogResult.OK && !lstCarpetas.Items.Cast<string>().Any(s => s.Equals(d.SelectedPath, StringComparison.OrdinalIgnoreCase)))
                        lstCarpetas.Items.Add(d.SelectedPath);
            };
            quitar.Click += delegate { if (lstCarpetas.SelectedIndex >= 0) lstCarpetas.Items.RemoveAt(lstCarpetas.SelectedIndex); };
            botonesCarpeta.Controls.Add(agregar); botonesCarpeta.Controls.Add(quitar);
            Fila(t, "Carpetas a depurar:", lstCarpetas, botonesCarpeta);

            numDias.Minimum = 1; numDias.Maximum = 28; numDias.Width = 70;
            numDias.ValueChanged += delegate { ActualizarEjemploDep(); };
            Fila(t, "Conservar de cada mes:", ConSufijo(numDias, "últimos días del mes"), null);
            lblDepEjemplo.ForeColor = Color.DimGray; lblDepEjemplo.AutoSize = true; lblDepEjemplo.MaximumSize = new Size(420, 0);
            Fila(t, "", lblDepEjemplo, null);

            numMin.Minimum = 0; numMin.Maximum = 365; numMin.Width = 70;
            Fila(t, "Conservar siempre:", ConSufijo(numMin, "respaldos más recientes de cada carpeta"), null);

            Fila(t, "Archivos a revisar:", txtDepFiltro, null);
            Fila(t, "", Ayuda("* = todos   ·   *.rar;*.zip = solo comprimidos"), null);

            chkFechaMod.Text = "Si el nombre no tiene fecha, usar la fecha de modificación del archivo";
            chkFechaMod.AutoSize = true;
            Fila(t, "", chkFechaMod, null);
            Fila(t, "", Ayuda("Fechas reconocidas en el nombre: 2026-11-09, 2026_11_09, 20261109, 09-11-2026, 09.11.2026"), null);

            numDia.Minimum = 1; numDia.Maximum = 28; numDia.Width = 70;
            HoraPicker(dtpDepHora);
            var cuando = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), WrapContents = false };
            cuando.Controls.Add(new Label { Text = "Día", AutoSize = true, Margin = new Padding(3, 7, 3, 0) });
            cuando.Controls.Add(numDia);
            cuando.Controls.Add(new Label { Text = "a las", AutoSize = true, Margin = new Padding(8, 7, 3, 0) });
            cuando.Controls.Add(dtpDepHora);
            Fila(t, "Ejecutar cada mes el:", cuando, null);

            var guardar = new Button(); ConfigBoton(guardar, "Guardar y programar", 170); guardar.Click += delegate { GuardarDepuracion(); };
            var previa = new Button(); ConfigBoton(previa, "Vista previa", 120); previa.Click += delegate { Previsualizar(false); };
            var ahora = new Button(); ConfigBoton(ahora, "Depurar ahora", 125); ahora.Click += delegate { Previsualizar(true); };
            var quitarProg = new Button(); ConfigBoton(quitarProg, "Quitar programación", 160);
            quitarProg.Click += delegate { Tarea.Borrar(Tarea.Depuracion); Util.Log("Depuración: programación quitada."); ActualizarEstado(); };
            FilaBotones(t, guardar, previa, ahora, quitarProg);
            FilaEstado(t, lblDepEstado);
        }

        // ---------- ayudas de diseño
        Button Boton(string texto, EventHandler click)
        {
            var b = new Button { Text = texto, Dock = DockStyle.Fill, Height = 28 };
            b.Click += click; return b;
        }

        static Label Ayuda(string texto) { return new Label { Text = texto, ForeColor = Color.DimGray, AutoSize = true, MaximumSize = new Size(420, 0) }; }

        static Control ConSufijo(Control c, string texto)
        {
            var p = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), WrapContents = false };
            p.Controls.Add(c);
            p.Controls.Add(new Label { Text = texto, AutoSize = true, Margin = new Padding(6, 7, 0, 0) });
            return p;
        }

        static void HoraPicker(DateTimePicker d)
        {
            d.Format = DateTimePickerFormat.Custom; d.CustomFormat = "HH:mm"; d.ShowUpDown = true; d.Width = 80;
        }

        void ConfigBoton(Button b, string texto, int ancho) { b.Text = texto; b.Width = ancho; b.Height = 32; }

        void Fila(TableLayoutPanel t, string etiqueta, Control c, Control extra)
        {
            int r = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(new Label { Text = etiqueta, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(0, 7, 4, 0) }, 0, r);
            if (c is TextBox || c is ComboBox || c is ListBox) c.Dock = DockStyle.Fill;
            c.Margin = new Padding(3, 4, 3, 4);
            t.Controls.Add(c, 1, r);
            if (extra != null) t.Controls.Add(extra, 2, r);
        }

        void FilaBotones(TableLayoutPanel t, params Button[] botones)
        {
            var p = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 12, 0, 4) };
            p.Controls.AddRange(botones);
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(p, 0, t.RowCount); t.SetColumnSpan(p, 3); t.RowCount++;
        }

        void FilaEstado(TableLayoutPanel t, Label l)
        {
            l.AutoSize = true; l.Font = new Font(Font, FontStyle.Bold); l.Margin = new Padding(3, 6, 3, 3);
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(l, 0, t.RowCount); t.SetColumnSpan(l, 3); t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.Controls.Add(new Panel(), 0, t.RowCount); t.RowCount++;
        }

        void ElegirCarpeta(TextBox tb)
        {
            using (var d = new FolderBrowserDialog { SelectedPath = tb.Text, ShowNewFolderButton = true })
                if (d.ShowDialog(this) == DialogResult.OK) tb.Text = d.SelectedPath;
        }

        void ElegirRar()
        {
            using (var d = new OpenFileDialog { Filter = "Rar.exe|Rar.exe|Programas (*.exe)|*.exe", FileName = txtRar.Text })
                if (d.ShowDialog(this) == DialogResult.OK) txtRar.Text = d.FileName;
        }

        // ---------- datos
        Config LeerPantalla()
        {
            return new Config
            {
                Origen = txtOrigen.Text.Trim(), Destino = txtDestino.Text.Trim(),
                Filtro = string.IsNullOrWhiteSpace(txtFiltro.Text) ? "*" : txtFiltro.Text.Trim(),
                Prefijo = txtPrefijo.Text.Trim(), Formato = cmbFormato.SelectedIndex == 1 ? "ZIP" : "RAR",
                RarPath = txtRar.Text.Trim(), DiaAnterior = cmbFecha.SelectedIndex == 0,
                Hora = dtpHora.Value.ToString("HH:mm"),
                DepCarpetas = lstCarpetas.Items.Cast<string>().ToList(),
                DepFiltro = string.IsNullOrWhiteSpace(txtDepFiltro.Text) ? "*" : txtDepFiltro.Text.Trim(),
                DepDiasFinMes = (int)numDias.Value, DepMinRecientes = (int)numMin.Value,
                DepUsarFechaMod = chkFechaMod.Checked, DepDia = (int)numDia.Value,
                DepHora = dtpDepHora.Value.ToString("HH:mm")
            };
        }

        static DateTime HoraDe(string s, int def)
        {
            DateTime h;
            return DateTime.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out h)
                ? DateTime.Today.Add(h.TimeOfDay) : DateTime.Today.AddHours(def);
        }

        void Cargar()
        {
            var c = Config.Cargar();
            txtOrigen.Text = c.Origen; txtDestino.Text = c.Destino; txtFiltro.Text = c.Filtro;
            txtPrefijo.Text = c.Prefijo; txtRar.Text = c.RarPath;
            cmbFormato.SelectedIndex = c.Formato == "ZIP" ? 1 : 0;
            cmbFecha.SelectedIndex = c.DiaAnterior ? 0 : 1;
            dtpHora.Value = HoraDe(c.Hora, 1);

            lstCarpetas.Items.Clear();
            foreach (var s in c.DepCarpetas) lstCarpetas.Items.Add(s);
            if (lstCarpetas.Items.Count == 0 && c.Destino != "") lstCarpetas.Items.Add(c.Destino);
            txtDepFiltro.Text = c.DepFiltro;
            numDias.Value = c.DepDiasFinMes; numMin.Value = c.DepMinRecientes;
            chkFechaMod.Checked = c.DepUsarFechaMod; numDia.Value = c.DepDia;
            dtpDepHora.Value = HoraDe(c.DepHora, 2);

            ActualizarEjemplo(); ActualizarEjemploDep(); ActualizarEstado(); CargarLog();
        }

        void ActualizarEjemplo()
        {
            var c = LeerPantalla();
            DateTime f = c.DiaAnterior ? DateTime.Today.AddDays(-1) : DateTime.Today;
            lblEjemplo.Text = "Ejemplo hoy: " + (c.Prefijo + " " + f.ToString("yyyy-MM-dd")).Trim() + (c.Formato == "ZIP" ? ".zip" : ".rar");
        }

        void ActualizarEjemploDep()
        {
            int n = (int)numDias.Value;
            DateTime mesPasado = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1);
            int dias = DateTime.DaysInMonth(mesPasado.Year, mesPasado.Month);
            var es = new CultureInfo("es-ES");
            string mes = mesPasado.ToString("MMMM yyyy", es);
            lblDepEjemplo.Text = "Ejemplo: de " + mes + " se conservan del " + (dias - n + 1) + " al " + dias +
                                 " y se borran del 1 al " + (dias - n) + ". El mes en curso nunca se toca.";
        }

        void ActualizarEstado()
        {
            var c = Config.Cargar();
            bool a = Tarea.Existe(Tarea.Respaldo);
            lblEstado.ForeColor = a ? Color.SeaGreen : Color.Firebrick;
            lblEstado.Text = a ? "● Programado: todos los días a las " + c.Hora : "● No programado";

            bool d = Tarea.Existe(Tarea.Depuracion);
            lblDepEstado.ForeColor = d ? Color.SeaGreen : Color.Firebrick;
            lblDepEstado.Text = d ? "● Programado: el día " + c.DepDia + " de cada mes a las " + c.DepHora : "● No programado";
        }

        void CargarLog()
        {
            try
            {
                if (!File.Exists(Util.LogPath)) { txtLog.Text = "(sin registros todavía)"; return; }
                var lineas = File.ReadAllLines(Util.LogPath, Encoding.UTF8);
                txtLog.Text = string.Join(Environment.NewLine, lineas.Reverse().Take(500));
            }
            catch (Exception ex) { txtLog.Text = ex.Message; }
        }

        void Aviso(string msg) { MessageBox.Show(this, msg, "Respaldo Diario", MessageBoxButtons.OK, MessageBoxIcon.Warning); }

        // ---------- respaldo
        bool ValidarRespaldo(Config c)
        {
            string error = null;
            if (c.Origen == "" || !Directory.Exists(c.Origen)) error = "Elige una carpeta de origen que exista.";
            else if (c.Destino == "") error = "Elige la carpeta de respaldo.";
            else if (string.Equals(Path.GetFullPath(c.Origen).TrimEnd('\\'), Path.GetFullPath(c.Destino).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                error = "La carpeta de respaldo no puede ser la misma que la de origen.";
            else if (c.Formato == "RAR" && !File.Exists(c.RarPath))
                error = "No se encontró Rar.exe. Instala WinRAR, indica la ruta de Rar.exe o elige formato ZIP.";
            if (error != null) Aviso(error);
            return error == null;
        }

        void GuardarRespaldo()
        {
            var c = LeerPantalla();
            if (!ValidarRespaldo(c)) return;
            try
            {
                c.Guardar();
                Tarea.CrearDiaria(c.Hora);
                Util.Log("Respaldo diario: programado a las " + c.Hora + ". Origen: " + c.Origen + "  Destino: " + c.Destino);
                MessageBox.Show(this, "Listo. El respaldo se ejecutará todos los días a las " + c.Hora + ".",
                                "Respaldo Diario", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { Aviso("No se pudo programar la tarea:\n" + ex.Message); }
            ActualizarEstado(); CargarLog();
        }

        void RespaldarAhora()
        {
            var c = LeerPantalla();
            if (!ValidarRespaldo(c)) return;
            int n = Respaldo.ArchivosPendientes(c).Count;
            if (n == 0) { MessageBox.Show(this, "No hay archivos en la carpeta de origen.", "Respaldo Diario"); return; }
            if (MessageBox.Show(this, "Se van a comprimir y mover " + n + " archivo(s). ¿Continuar?", "Respaldo Diario",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            UseWaitCursor = true; btnAhora.Enabled = btnGuardar.Enabled = false; btnAhora.Text = "Respaldando...";
            new Thread(() =>
            {
                string msg; bool ok;
                try { msg = Respaldo.Ejecutar(c); ok = true; }
                catch (Exception ex) { msg = "ERROR  " + ex.Message; ok = false; }
                Util.Log("[manual] " + msg);
                BeginInvoke((Action)(() =>
                {
                    UseWaitCursor = false; btnAhora.Enabled = btnGuardar.Enabled = true; btnAhora.Text = "Respaldar ahora";
                    CargarLog();
                    MessageBox.Show(this, msg, "Respaldo Diario", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
                }));
            }) { IsBackground = true }.Start();
        }

        // ---------- depuración
        bool ValidarDepuracion(Config c)
        {
            if (c.DepCarpetas.Count == 0) { Aviso("Agrega al menos una carpeta a depurar."); return false; }
            var origen = c.Origen == "" ? "" : Path.GetFullPath(c.Origen).TrimEnd('\\');
            if (origen != "" && c.DepCarpetas.Any(s => Path.GetFullPath(s).TrimEnd('\\').Equals(origen, StringComparison.OrdinalIgnoreCase)))
            { Aviso("No agregues la carpeta de origen del respaldo diario: ahí llegan los archivos nuevos."); return false; }
            return true;
        }

        void GuardarDepuracion()
        {
            var c = LeerPantalla();
            if (!ValidarDepuracion(c)) return;
            try
            {
                c.Guardar();
                Tarea.CrearMensual(c.DepDia, c.DepHora);
                Util.Log("Depuración: programada el día " + c.DepDia + " a las " + c.DepHora + ". Conservar últimos " + c.DepDiasFinMes +
                         " días de cada mes. Carpetas: " + string.Join(" | ", c.DepCarpetas));
                MessageBox.Show(this, "Listo. La depuración se ejecutará el día " + c.DepDia + " de cada mes a las " + c.DepHora +
                                ".\n\nTe recomiendo revisar antes la \"Vista previa\".", "Respaldo Diario", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { Aviso("No se pudo programar la tarea:\n" + ex.Message); }
            ActualizarEstado(); CargarLog();
        }

        void Previsualizar(bool permitirBorrar)
        {
            var c = LeerPantalla();
            if (!ValidarDepuracion(c)) return;
            ResultadoDepuracion r;
            try { UseWaitCursor = true; r = Depuracion.Analizar(c); }
            catch (Exception ex) { Aviso(ex.Message); return; }
            finally { UseWaitCursor = false; }

            using (var v = new VistaPrevia(r, permitirBorrar))
                if (v.ShowDialog(this) == DialogResult.OK)
                {
                    Util.Log("[manual] Depuración iniciada.");
                    string msg = Depuracion.Borrar(r);
                    Util.Log("[manual] " + msg);
                    CargarLog();
                    MessageBox.Show(this, msg, "Respaldo Diario", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
        }
    }

    // ------------------------------------------------------------------ Entrada
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Any(a => a.Equals("/auto", StringComparison.OrdinalIgnoreCase)))
            {
                try { Util.Log("[auto] " + Respaldo.Ejecutar(Config.Cargar())); return 0; }
                catch (Exception ex) { Util.Log("[auto] ERROR  " + ex.Message); return 1; }
            }

            if (args.Any(a => a.Equals("/depurar", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var c = Config.Cargar();
                    if (c.DepCarpetas.Count == 0) { Util.Log("[depurar] No hay carpetas configuradas."); return 0; }
                    var r = Depuracion.Analizar(c);
                    Util.Log("[depurar] Inicio. Conservar últimos " + c.DepDiasFinMes + " días de cada mes.");
                    foreach (var a in r.Avisos) Util.Log("[depurar] AVISO  " + a);
                    Util.Log("[depurar] " + Depuracion.Borrar(r));
                    return 0;
                }
                catch (Exception ex) { Util.Log("[depurar] ERROR  " + ex.Message); return 1; }
            }

            // La ventana necesita permisos de administrador para crear las tareas programadas
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            {
                try { Process.Start(new ProcessStartInfo(Application.ExecutablePath) { Verb = "runas", UseShellExecute = true }); }
                catch { MessageBox.Show("Respaldo Diario necesita permisos de administrador.", "Respaldo Diario"); }
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Ventana());
            return 0;
        }
    }
}
