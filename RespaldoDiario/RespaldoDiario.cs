// Respaldo Diario - comprime los archivos de una carpeta en "Respaldo AAAA-MM-DD.rar"
// y los mueve a una carpeta de respaldo. Se programa en el Programador de tareas de Windows.
//
//   RespaldoDiario.exe          -> abre la ventana de configuración
//   RespaldoDiario.exe /auto    -> ejecuta el respaldo sin ventana (lo usa la tarea programada)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Respaldo Diario")]
[assembly: System.Reflection.AssemblyProduct("Respaldo Diario")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace RespaldoDiario
{
    // ------------------------------------------------------------------ Configuración
    class Config
    {
        public string Origen = "";
        public string Destino = "";
        public string Filtro = "*";
        public string Prefijo = "Respaldo";
        public string Formato = "RAR";          // RAR o ZIP
        public string RarPath = "";
        public bool DiaAnterior = true;         // a la 1 AM los archivos son del día anterior
        public string Hora = "01:00";

        public static string Carpeta
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.CommonApplicationData), "RespaldoDiario");
            }
        }
        static string Archivo { get { return Path.Combine(Carpeta, "config.ini"); } }

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
                "DiaAnterior=" + (DiaAnterior ? "1" : "0"), "Hora=" + Hora
            }, Encoding.UTF8);
        }
    }

    // ------------------------------------------------------------------ Lógica del respaldo
    static class Respaldo
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
            var dir = new DirectoryInfo(c.Origen);
            string filtro = string.IsNullOrEmpty(c.Filtro) ? "*" : c.Filtro;
            return dir.GetFiles(filtro)
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

        /// Devuelve un mensaje con el resultado. Lanza excepción si algo falla.
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

    // ------------------------------------------------------------------ Tarea programada
    static class Tarea
    {
        public const string Nombre = "RespaldoDiario";

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

        public static bool Existe() { string s; return Schtasks("/query /tn " + Nombre, out s) == 0; }

        public static void Crear(string hora)
        {
            string exe = Application.ExecutablePath;
            string args = "/create /f /tn " + Nombre + " /sc daily /st " + hora +
                          " /ru SYSTEM /rl HIGHEST /tr \"\\\"" + exe + "\\\" /auto\"";
            string s;
            if (Schtasks(args, out s) != 0) throw new Exception(s.Trim());
        }

        public static void Borrar() { string s; Schtasks("/delete /f /tn " + Nombre, out s); }
    }

    // ------------------------------------------------------------------ Ventana
    class Ventana : Form
    {
        TextBox txtOrigen = new TextBox(), txtDestino = new TextBox(), txtFiltro = new TextBox(),
                txtPrefijo = new TextBox(), txtRar = new TextBox(), txtLog = new TextBox();
        ComboBox cmbFormato = new ComboBox(), cmbFecha = new ComboBox();
        DateTimePicker dtpHora = new DateTimePicker();
        Label lblEstado = new Label(), lblEjemplo = new Label();
        Button btnGuardar = new Button(), btnAhora = new Button(), btnQuitar = new Button();

        public Ventana()
        {
            Text = "Respaldo Diario";
            Font = new Font("Segoe UI", 9.5f);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(640, 560);
            MinimumSize = new Size(560, 520);

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 3, AutoSize = false };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            Controls.Add(t);

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
            var ayuda = new Label { Text = "* = todos   ·   *.csv = solo CSV", ForeColor = Color.DimGray, AutoSize = true };
            Fila(t, "", ayuda, null);

            dtpHora.Format = DateTimePickerFormat.Custom; dtpHora.CustomFormat = "HH:mm";
            dtpHora.ShowUpDown = true; dtpHora.Width = 90;
            Fila(t, "Ejecutar todos los días a las:", dtpHora, null);

            var botones = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 10, 0, 4) };
            ConfigBoton(btnGuardar, "Guardar y programar", 170); btnGuardar.Click += delegate { Guardar(); };
            ConfigBoton(btnAhora, "Respaldar ahora", 140); btnAhora.Click += delegate { RespaldarAhora(); };
            ConfigBoton(btnQuitar, "Quitar programación", 160); btnQuitar.Click += delegate { Tarea.Borrar(); ActualizarEstado(); };
            botones.Controls.AddRange(new Control[] { btnGuardar, btnAhora, btnQuitar });
            t.Controls.Add(botones, 0, t.RowCount); t.SetColumnSpan(botones, 3); t.RowCount++;

            lblEstado.AutoSize = true; lblEstado.Font = new Font(Font, FontStyle.Bold);
            t.Controls.Add(lblEstado, 0, t.RowCount); t.SetColumnSpan(lblEstado, 3); t.RowCount++;

            txtLog.Multiline = true; txtLog.ReadOnly = true; txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.Dock = DockStyle.Fill; txtLog.Font = new Font("Consolas", 9f); txtLog.BackColor = SystemColors.Window;
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.Controls.Add(txtLog, 0, t.RowCount); t.SetColumnSpan(txtLog, 3); t.RowCount++;

            Cargar();
        }

        Button Boton(string texto, EventHandler click)
        {
            var b = new Button { Text = texto, Dock = DockStyle.Fill, Height = 28 };
            b.Click += click; return b;
        }

        void ConfigBoton(Button b, string texto, int ancho) { b.Text = texto; b.Width = ancho; b.Height = 32; }

        void Fila(TableLayoutPanel t, string etiqueta, Control c, Control extra)
        {
            int r = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(new Label { Text = etiqueta, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 4, 0) }, 0, r);
            if (!(c is Label) && !(c is DateTimePicker)) c.Dock = DockStyle.Fill;
            c.Margin = new Padding(3, 4, 3, 4);
            t.Controls.Add(c, 1, r);
            if (extra != null) t.Controls.Add(extra, 2, r);
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

        Config LeerPantalla()
        {
            return new Config
            {
                Origen = txtOrigen.Text.Trim(), Destino = txtDestino.Text.Trim(),
                Filtro = string.IsNullOrWhiteSpace(txtFiltro.Text) ? "*" : txtFiltro.Text.Trim(),
                Prefijo = txtPrefijo.Text.Trim(), Formato = cmbFormato.SelectedIndex == 1 ? "ZIP" : "RAR",
                RarPath = txtRar.Text.Trim(), DiaAnterior = cmbFecha.SelectedIndex == 0,
                Hora = dtpHora.Value.ToString("HH:mm")
            };
        }

        void Cargar()
        {
            var c = Config.Cargar();
            txtOrigen.Text = c.Origen; txtDestino.Text = c.Destino; txtFiltro.Text = c.Filtro;
            txtPrefijo.Text = c.Prefijo; txtRar.Text = c.RarPath;
            cmbFormato.SelectedIndex = c.Formato == "ZIP" ? 1 : 0;
            cmbFecha.SelectedIndex = c.DiaAnterior ? 0 : 1;
            DateTime h;
            dtpHora.Value = DateTime.TryParse(c.Hora, out h) ? DateTime.Today.Add(h.TimeOfDay) : DateTime.Today.AddHours(1);
            ActualizarEjemplo(); ActualizarEstado(); CargarLog();
        }

        void ActualizarEjemplo()
        {
            var c = LeerPantalla();
            DateTime f = c.DiaAnterior ? DateTime.Today.AddDays(-1) : DateTime.Today;
            lblEjemplo.Text = "Ejemplo hoy: " + (c.Prefijo + " " + f.ToString("yyyy-MM-dd")).Trim() + (c.Formato == "ZIP" ? ".zip" : ".rar");
        }

        void ActualizarEstado()
        {
            bool activa = Tarea.Existe();
            lblEstado.ForeColor = activa ? Color.SeaGreen : Color.Firebrick;
            lblEstado.Text = activa ? "● Programado: se ejecuta todos los días a las " + Config.Cargar().Hora
                                    : "● No programado";
        }

        void CargarLog()
        {
            try
            {
                if (!File.Exists(Respaldo.LogPath)) { txtLog.Text = "(sin registros todavía)"; return; }
                var lineas = File.ReadAllLines(Respaldo.LogPath, Encoding.UTF8);
                txtLog.Text = string.Join(Environment.NewLine, lineas.Reverse().Take(200));
            }
            catch (Exception ex) { txtLog.Text = ex.Message; }
        }

        bool Validar(Config c)
        {
            string error = null;
            if (c.Origen == "" || !Directory.Exists(c.Origen)) error = "Elige una carpeta de origen que exista.";
            else if (c.Destino == "") error = "Elige la carpeta de respaldo.";
            else if (string.Equals(Path.GetFullPath(c.Origen).TrimEnd('\\'), Path.GetFullPath(c.Destino).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                error = "La carpeta de respaldo no puede ser la misma que la de origen.";
            else if (c.Formato == "RAR" && !File.Exists(c.RarPath))
                error = "No se encontró Rar.exe. Instala WinRAR, indica la ruta de Rar.exe o elige formato ZIP.";
            if (error != null) MessageBox.Show(this, error, "Respaldo Diario", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return error == null;
        }

        void Guardar()
        {
            var c = LeerPantalla();
            if (!Validar(c)) return;
            try
            {
                c.Guardar();
                Tarea.Crear(c.Hora);
                Respaldo.Log("Configuración guardada. Programado diario a las " + c.Hora + ". Origen: " + c.Origen + "  Destino: " + c.Destino);
                MessageBox.Show(this, "Listo. El respaldo se ejecutará todos los días a las " + c.Hora + ".",
                                "Respaldo Diario", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "No se pudo programar la tarea:\n" + ex.Message, "Respaldo Diario", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            ActualizarEstado(); CargarLog();
        }

        void RespaldarAhora()
        {
            var c = LeerPantalla();
            if (!Validar(c)) return;
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
                Respaldo.Log("[manual] " + msg);
                BeginInvoke((Action)(() =>
                {
                    UseWaitCursor = false; btnAhora.Enabled = btnGuardar.Enabled = true; btnAhora.Text = "Respaldar ahora";
                    CargarLog();
                    MessageBox.Show(this, msg, "Respaldo Diario", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
                }));
            }) { IsBackground = true }.Start();
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
                try { Respaldo.Log("[auto] " + Respaldo.Ejecutar(Config.Cargar())); return 0; }
                catch (Exception ex) { Respaldo.Log("[auto] ERROR  " + ex.Message); return 1; }
            }

            // La ventana necesita permisos de administrador para crear la tarea programada
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Application.ExecutablePath) { Verb = "runas", UseShellExecute = true });
                }
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
