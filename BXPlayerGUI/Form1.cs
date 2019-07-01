﻿using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;
using System.Xml;
using System.Threading;
using BXPlayer;
using BXPlayerEvents;
using System.ComponentModel;
using System.Text;
using System.Reflection;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Xml.Serialization;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Security.Principal;
using System.Security.AccessControl;

namespace BXPlayerGUI
{
    public partial class Form1 : Form
    {
        private const string MutexName = "62ba6bfa-9bb9-11e9-a2a3-2a2ae2dbcce4";
        private const string PipeName = "d4229cee-9bba-11e9-a2a3-2a2ae2dbcce4";
        private readonly object _namedPiperServerThreadLock = new object();
        private Mutex _mutexApplication;
        private bool _firstApplicationInstance;
        private readonly string cwd = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName) + "\\";
        private readonly string bxpatch_dest = Environment.GetEnvironmentVariable("WINDIR") + "\\patches.hsb";
        private readonly string[] args = Environment.GetCommandLineArgs();
        private readonly string _patchswitcher_exe = "BXPatchSwitcher.exe";
        private readonly string patches_dir;
        private readonly string bankfile;
        private readonly BXPlayerClass bx;
        private TcpListener tcp;
        private string current_hash;
        private string current_file;
        private Stream current_datastream = null;
        private int default_tempo;
        private int http_port = 59999;
        private bool settingReverbCB = false;
        private bool http_ready = false;
        private bool seekbar_held = false;
        private bool play_splash = false;
        private readonly int default_reverb = 0;
        private NamedPipeServerStream _namedPipeServerStream;
        private NamedPipeXmlPayload _namedPipeXmlPayload;
        public string version;

        private bool IsApplicationFirstInstance()
        {
            // Allow for multiple runs but only try and get the mutex once
            if (_mutexApplication == null)
            {
                _mutexApplication = new Mutex(true, MutexName, out _firstApplicationInstance);
            }

            return _firstApplicationInstance;
        }

        public Form1()
        {
            if (!IsApplicationFirstInstance())
            {
                Debug.WriteLine("Not first instance!");

                // first index is always executable
                if (Environment.GetCommandLineArgs().Length > 1) {
                    Debug.WriteLine("Sending CLI arguments to other instance...");
                    NamedPipeClientSendOptions(new NamedPipeXmlPayload
                    {
                        CommandLineArguments = new List<string>(Environment.GetCommandLineArgs())
                    });
                }

                // Stop loading form and quit
                Close();
                return;
            }

            InitializeComponent();
            Assembly assembly = Assembly.GetExecutingAssembly();
            version = FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion;
            Text += " v" + version;
            Debug.WriteLine(Text + " initializing");

            Debug.WriteLine("CWD is " + cwd);
            patches_dir = cwd + "BXBanks\\";
            bankfile = patches_dir + "BXBanks.xml";
            bx = new BXPlayerClass();
            NamedPipeServerCreateServer();
        }

        private void NamedPipeServerCreateServer()
        {
            // Create a new pipe accessible by local authenticated users, disallow network
            var sidNetworkService = new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);
            var sidWorld = new SecurityIdentifier(WellKnownSidType.WorldSid, null);

            var pipeSecurity = new PipeSecurity();

            // Deny network access to the pipe
            var accessRule = new PipeAccessRule(sidNetworkService, PipeAccessRights.ReadWrite, AccessControlType.Deny);
            pipeSecurity.AddAccessRule(accessRule);

            // Allow Everyone to read/write
            accessRule = new PipeAccessRule(sidWorld, PipeAccessRights.ReadWrite, AccessControlType.Allow);
            pipeSecurity.AddAccessRule(accessRule);

            // Current user is the owner
            SecurityIdentifier sidOwner = WindowsIdentity.GetCurrent().Owner;
            if (sidOwner != null)
            {
                accessRule = new PipeAccessRule(sidOwner, PipeAccessRights.FullControl, AccessControlType.Allow);
                pipeSecurity.AddAccessRule(accessRule);
            }

            // Create pipe and start the async connection wait
            _namedPipeServerStream = new NamedPipeServerStream(
                PipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                0,
                0,
                pipeSecurity);

            // Begin async wait for connections
            _namedPipeServerStream.BeginWaitForConnection(NamedPipeServerConnectionCallback, _namedPipeServerStream);
        }

        /// <summary>
        ///     The function called when a client connects to the named pipe. Note: This method is called on a non-UI thread.
        /// </summary>
        /// <param name="iAsyncResult"></param>
        private void NamedPipeServerConnectionCallback(IAsyncResult iAsyncResult)
        {
            try
            {
                // End waiting for the connection
                _namedPipeServerStream.EndWaitForConnection(iAsyncResult);

                // Read data and prevent access to _namedPipeXmlPayload during threaded operations
                lock (_namedPiperServerThreadLock)
                {
                    // Read data from client
                    var xmlSerializer = new XmlSerializer(typeof(NamedPipeXmlPayload));
                    _namedPipeXmlPayload = (NamedPipeXmlPayload)xmlSerializer.Deserialize(_namedPipeServerStream);

                    // _namedPipeXmlPayload contains the data sent from the other instance
                    try
                    {
                        if (_namedPipeXmlPayload.CommandLineArguments.Count > 1)
                        {
                            if (File.Exists(_namedPipeXmlPayload.CommandLineArguments[1]))
                            {
                                PlayFile(_namedPipeXmlPayload.CommandLineArguments[1], GetCheckBoxChecked(loopcb));
                            } else
                            {
                                ProcessStartupOptions(_namedPipeXmlPayload.CommandLineArguments[1]);
                            }
                        }
                    }
                    catch (Exception e) {
                        Debug.WriteLine("Failed to process incoming named pipe message: " + e.Message);
                    }
                    Activate();
                }
            }
            catch (ObjectDisposedException)
            {
                // EndWaitForConnection will exception when someone closes the pipe before connection made
                // In that case we dont create any more pipes and just return
                // This will happen when app is closing and our pipe is closed/disposed
                return;
            }
            catch (Exception)
            {
                // ignored
            }
            finally
            {
                // Close the original pipe (we will create a new one each time)
                _namedPipeServerStream.Dispose();
            }

            // Create a new pipe for next connection
            NamedPipeServerCreateServer();
        }

        /// <summary>
        ///     Uses a named pipe to send the currently parsed options to an already running instance.
        /// </summary>
        /// <param name="namedPipePayload"></param>
        private void NamedPipeClientSendOptions(NamedPipeXmlPayload namedPipePayload)
        {
            try
            {
                using (var namedPipeClientStream = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    namedPipeClientStream.Connect(3000); // Maximum wait 3 seconds

                    var xmlSerializer = new XmlSerializer(typeof(NamedPipeXmlPayload));
                    xmlSerializer.Serialize(namedPipeClientStream, namedPipePayload);
                }
            }
            catch (Exception)
            {
                // Error connecting or sending
            }
        }

        private void VolumeControl_Scroll(object sender, EventArgs e)
        {
            TrackBar tb = (TrackBar)sender;
            SetVolume(tb.Value);
        }

        private void TempoControl_Scroll(object sender, EventArgs e)
        {
            TrackBar tb = (TrackBar)sender;
            SetTempo(tb.Value);
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            bool patchloaded = true;
            if (File.Exists(bxpatch_dest))
            {
                try
                {
                    current_hash = ZefieLib.Cryptography.Hash.SHA1(bxpatch_dest);
                    Debug.WriteLine("Current Patches Hash: " + current_hash);

                    if (File.Exists(bankfile))
                    {
                        using (XmlReader reader = XmlReader.Create(bankfile))
                        {
                            while (reader.Read())
                            {
                                if (reader.NodeType == XmlNodeType.Element)
                                {
                                    if (reader.Name == "bank")
                                    {
                                        string patchfile = patches_dir + reader.GetAttribute("src");
                                        string patchname = reader.GetAttribute("name");
                                        string patchhash = reader.GetAttribute("sha1").ToLower();
                                        if (patchhash == current_hash)
                                        {
                                            string splash = reader.GetAttribute("splash");
                                            try
                                            {
                                                play_splash = Convert.ToBoolean(splash);
                                                Debug.WriteLine("splash compatible bank found. play_splash = " + play_splash.ToString());
                                            }
                                            catch
                                            {
                                                Debug.WriteLine("splash was not a boolean");
                                            };
                                            Debug.WriteLine("Detected " + patchname + " as currently installed");
                                            SetLabelText(bxinsthsb, patchname);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                    patchloaded = false;
                }
            }
            else
            {
                patchloaded = false;
            }
            if (patchloaded)
            {
                bx.MetaDataChanged += Bx_MetaDataChanged;
                bx.FileChanged += Bx_FileChanged;
                bx.PlayStateChanged += Bx_PlayStateChanged;
                bx.ProgressChanged += Bx_ProgressChanged;
                bx.BXInit();
                string bxvers = bx.BeatnikVersion;
                SetLabelText(bxversionlbl, "v" + bxvers);

                // 2.0.0+ reverbs
                if (Convert.ToInt32(bxvers.Substring(0, 1)) >= 2)
                {
                    reverbcb.Items.Add("Early Reflections");
                    reverbcb.Items.Add("Basement");
                    reverbcb.Items.Add("Banquet Hall");
                    reverbcb.Items.Add("Catacombs ");
                }
                settingReverbCB = true;
                SetComboBoxIndex(reverbcb, default_reverb);
                if (args.Length > 1)
                {
                    if (File.Exists(args[1]))
                    {
                        PlayFile(args[1], loopcb.Checked);
                    }
                    else
                    {
                        ProcessStartupOptions(args[1]);
                    }
                }
                else
                {
                    if (play_splash)
                    {
                        current_datastream = new MemoryStream(Properties.Resources.Splash);
                        SetBXParams();
                        PlayFile(current_datastream, "Splash.mid", false);
                        play_splash = false;
                    }
                }
            }
            else
            {
                Debug.WriteLine("WARN: No patches installed!");
                SetLabelText(bxinsthsb, "None");
                SetControlEnabled(loopcb, false);
                SetControlEnabled(openfile, false);
            }
        }

        private void ProcessStartupOptions(string serialized_data)
        {
            try
            {
                // session data comes back without the exe in slot 0

                string[] options = Encoding.UTF8.GetString(ZefieLib.Data.Base64Decode(serialized_data)).Split('|');
                SetCheckBoxChecked(loopcb, Convert.ToBoolean(options[5]));
                if (options[0].Length > 0)
                {
                    PlayFile(options[0], loopcb.Checked);
                }

                BackgroundWorker bw = new BackgroundWorker();
                bw.DoWork += new DoWorkEventHandler(
                    delegate (object o, DoWorkEventArgs arg)
                    {
                        while (bx.Duration == 0 && bx.PlayState == PlayState.Playing)
                        {
                            // fucking terrible I know
                            Thread.Sleep(100);
                        }

                        bx.Volume = Convert.ToInt32(options[1]);
                        if (bx.PlayState != PlayState.Stopped)
                        {
                            bx.Tempo = Convert.ToInt32(options[2]);
                            bx.Transpose = Convert.ToInt32(options[3]);
                            bx.Position = Convert.ToInt32(options[4]);
                        }
                        SetCheckBoxChecked(midichk_1, Convert.ToBoolean(options[6]));
                        SetCheckBoxChecked(midichk_2, Convert.ToBoolean(options[7]));
                        SetCheckBoxChecked(midichk_3, Convert.ToBoolean(options[8]));
                        SetCheckBoxChecked(midichk_4, Convert.ToBoolean(options[9]));
                        SetCheckBoxChecked(midichk_5, Convert.ToBoolean(options[10]));
                        SetCheckBoxChecked(midichk_6, Convert.ToBoolean(options[11]));
                        SetCheckBoxChecked(midichk_7, Convert.ToBoolean(options[12]));
                        SetCheckBoxChecked(midichk_8, Convert.ToBoolean(options[13]));
                        SetCheckBoxChecked(midichk_9, Convert.ToBoolean(options[14]));
                        SetCheckBoxChecked(midichk_10, Convert.ToBoolean(options[15]));
                        SetCheckBoxChecked(midichk_11, Convert.ToBoolean(options[16]));
                        SetCheckBoxChecked(midichk_12, Convert.ToBoolean(options[17]));
                        SetCheckBoxChecked(midichk_13, Convert.ToBoolean(options[18]));
                        SetCheckBoxChecked(midichk_14, Convert.ToBoolean(options[19]));
                        SetCheckBoxChecked(midichk_15, Convert.ToBoolean(options[20]));
                        SetCheckBoxChecked(midichk_16, Convert.ToBoolean(options[21]));
                        SetComboBoxIndex(reverbcb, Convert.ToInt32(options[22]));
                        GC.Collect();
                    }
                );
                bw.RunWorkerAsync();

            }
            catch { }
        }

        private void Bw_DoWork(object sender, DoWorkEventArgs e)
        {
            throw new NotImplementedException();
        }

        private string SerializeData(bool full = true)
        {
            string options = Process.GetCurrentProcess().MainModule.FileName + "|" +
                current_file;

            if (full)
            {
                options += "|" +
                bx.Volume.ToString() + "|" +
                bx.Tempo.ToString() + "|" +
                bx.Transpose.ToString() + "|" +
                bx.Position.ToString() + "|" +
                loopcb.Checked.ToString() + "|" +
                midichk_1.Checked.ToString() + "|" +
                midichk_2.Checked.ToString() + "|" +
                midichk_3.Checked.ToString() + "|" +
                midichk_4.Checked.ToString() + "|" +
                midichk_5.Checked.ToString() + "|" +
                midichk_6.Checked.ToString() + "|" +
                midichk_7.Checked.ToString() + "|" +
                midichk_8.Checked.ToString() + "|" +
                midichk_9.Checked.ToString() + "|" +
                midichk_10.Checked.ToString() + "|" +
                midichk_11.Checked.ToString() + "|" +
                midichk_12.Checked.ToString() + "|" +
                midichk_13.Checked.ToString() + "|" +
                midichk_14.Checked.ToString() + "|" +
                midichk_15.Checked.ToString() + "|" +
                midichk_16.Checked.ToString() + "|" +
                reverbcb.SelectedIndex;
            }

            return ZefieLib.Data.Base64Encode(options);
        }


        private void Bx_ProgressChanged(object sender, ProgressEvent e)
        {
            //Debug.WriteLine("progresschanged fired (seekbar_held: " + seekbar_held.ToString() + ")");
            SetLabelText(progresslbl, FormatTime(e.Position));

            if (!seekbar_held)
            {
                SetTrackbarValue(seekbar, e.Position);
            }
        }

        private void Bx_PlayStateChanged(object sender, PlayStateEvent e)
        {
            Debug.WriteLine("playstatechange fired ~ PlayState: " + e.State);
            if (e.State != PlayState.Stopped)
            {
                SetControlVisiblity(mainControlPanel, true);
                if (e.State == PlayState.Paused)
                {
                    SetButtonEnabled(playbut, true);
                    SetButtonEnabled(stopbut, true);
                    SetButtonImage(playbut, Properties.Resources.icon_play);
                    SetLabelText(status, "Paused.");
                }
                if (e.State == PlayState.Playing)
                {
                    SetBXParams();
                    SetButtonEnabled(playbut, true);
                    SetButtonEnabled(stopbut, true);
                    SetButtonImage(playbut, Properties.Resources.icon_pause);
                    SetLabelText(status, "Playing.");
                }
            }
            else
            {
                SetControlVisiblity(mainControlPanel, false);
                SetButtonEnabled(playbut, true);
                SetButtonEnabled(stopbut, false);
                SetButtonImage(playbut, Properties.Resources.icon_play);
                SetLabelText(status, "Ready.");
            }
        }

        private void Bx_FileChanged(object sender, FileChangeEvent e)
        {
            Debug.WriteLine("filechanged fired");
            default_tempo = e.Tempo;
            SetTrackbarValue(tempoControl, e.Tempo);
            SetTrackbarValue(transposeControl, 0);
            SetLabelText(transposevalbl, "0");
            SetLabelText(durationlbl, FormatTime(e.Duration));
            SetTrackbarValue(seekbar, 0, e.Duration);
            SetLabelText(statusfile, Path.GetFileName(e.File));
            SetLabelText(tempovallbl, e.Tempo + "BPM");
            SetControlVisiblity(mainControlPanel, true);
            SetButtonEnabled(infobut, (Path.GetExtension(e.File).ToLower() == ".rmf"));
            if (e.LoadedFile.StartsWith("http://"))
            {
                long res = DeleteUrlCacheEntry(e.LoadedFile);
                Debug.WriteLine("Deleted " + res.ToString() + " files from disk cache for " + e.LoadedFile);
            }

            settingReverbCB = true;
        }

        private void Bx_MetaDataChanged(object sender, MetaDataEvent e)
        {
            if (e.Title != null)
            {
                SetLabelText(statustitle, e.Title);
            }
            Debug.WriteLine(e.RawMeta.Key + ": " + e.RawMeta.Value);
        }
        public string FormatTime(int ms, bool seconds = false)
        {
            TimeSpan t = seconds ? TimeSpan.FromSeconds(ms) : TimeSpan.FromMilliseconds(ms);
            return string.Format("{0:D1}:{1:D2}", t.Minutes, t.Seconds);
        }

        private void SetComboBoxIndex(ComboBox cb, int index)
        {
            if (cb.InvokeRequired)
            {
                cb.Invoke(new MethodInvoker(delegate { cb.SelectedIndex = index; }));
            }
            else
            {
                cb.SelectedIndex = index;
            }
        }

        private void SetButtonEnabled(Button b, bool enabled)
        {
            if (b.InvokeRequired)
            {
                b.Invoke(new MethodInvoker(delegate { b.Enabled = enabled; }));
            }
            else
            {
                b.Enabled = enabled;
            }
        }

        private void SetButtonImage(Button b, Image image)
        {
            if (b.InvokeRequired)
            {
                b.Invoke(new MethodInvoker(delegate
                {
                    b.Image.Dispose();
                    b.Image = image;
                }));
            }
            else
            {
                b.Image.Dispose();
                b.Image = image;
            }
        }

        private void SetLabelText(Label l, string text)
        {
            if (l.InvokeRequired)
            {
                l.Invoke(new MethodInvoker(delegate { l.Text = text; }));
            }
            else
            {
                l.Text = text;
            }
        }
        private void SetLabelText(ToolStripStatusLabel l, string text)
        {
            l.Text = text;
        }

        private void SetTrackbarValue(TrackBar t, int value)
        {
            if (value <= t.Maximum && value >= t.Minimum)
            {
                if (t.InvokeRequired)
                {
                    t.Invoke(new MethodInvoker(delegate { t.Value = value; }));
                }
                else
                {
                    t.Value = value;
                }
            }
        }

        private void SetTrackbarValue(TrackBar t, int value, int max)
        {
            if (t.InvokeRequired)
            {
                t.Invoke(new MethodInvoker(delegate
                {
                    t.Maximum = max;
                    t.Value = value;
                }));
            }
            else
            {
                t.Maximum = max;
                t.Value = value;
            }
        }

        private int GetTrackbarValue(TrackBar t)
        {
            int value = -1;
            if (t.InvokeRequired)
            {
                t.Invoke(new MethodInvoker(delegate { value = t.Value; }));
            }
            else
            {
                value = t.Value;
            }
            return value;
        }

        private int GetComboBoxIndex(ComboBox t)
        {
            int value = -1;
            if (t.InvokeRequired)
            {
                t.Invoke(new MethodInvoker(delegate { value = t.SelectedIndex; }));
            }
            else
            {
                value = t.SelectedIndex;
            }
            return value;
        }

        private bool GetCheckBoxChecked(CheckBox t)
        {
            bool value = false;
            if (t.InvokeRequired)
            {
                t.Invoke(new MethodInvoker(delegate { value = t.Checked; }));
            }
            else
            {
                value = t.Checked;
            }
            return value;
        }

        private void SetCheckBoxChecked(CheckBox c, bool @checked)
        {
            if (c.InvokeRequired)
            {
                c.Invoke(new MethodInvoker(delegate { c.Checked = @checked; }));
            }
            else
            {
                c.Checked = @checked;
            }
        }

        private void SetControlVisiblity(Control c, bool visible)
        {
            if (c.InvokeRequired)
            {
                c.Invoke(new MethodInvoker(delegate { c.Visible = visible; }));
            }
            else
            {
                c.Visible = visible;
            }
        }

        private void SetControlEnabled(Control c, bool enabled)
        {
            if (c.InvokeRequired)
            {
                c.Invoke(new MethodInvoker(delegate { c.Enabled = enabled; }));
            }
            else
            {
                c.Enabled = enabled;
            }
        }

        private void Temporstbtn_Click(object sender, EventArgs e)
        {
            if (default_tempo >= 40)
            {
                SetTrackbarValue(tempoControl, default_tempo);
                SetTempo(default_tempo);
            }
        }


        private void SetTranspose(int val)
        {
            bx.Transpose = val;
            SetLabelText(transposevalbl, val.ToString());
        }

        private void SetTempo(int val)
        {
            bx.Tempo = val;
            SetLabelText(tempovallbl, val.ToString() + "BPM");
        }

        private void SetVolume(int val)
        {
            SetLabelText(volvallbl, val.ToString() + "%");
            bx.Volume = val;
        }

        private void Loopcb_CheckedChanged(object sender, EventArgs e)
        {
            if (bx.PlayState != PlayState.Stopped)
            {
                bx.Loop = loopcb.Checked;
            }
        }

        private void Seekbar_MouseUp(object sender, MouseEventArgs e)
        {
            seekbar_held = false;
            SetLabelText(seekpos, "");
            bx.Position = seekbar.Value;
            SetLabelText(progresslbl, FormatTime(bx.Position));
        }

        private void Seekbar_MouseDown(object sender, MouseEventArgs e)
        {
            seekbar_held = true;
        }

        private void Playbut_Click(object sender, EventArgs e)
        {
            PlayState bxstate = bx.PlayState;
            if (bxstate == PlayState.Stopped)
            {
                bx.Play();
                SetTrackbarValue(seekbar, bx.Position, bx.Duration);
                SetBXParams();
            }
            else
            {
                bx.PlayPause();
            }
        }

        private void SetBXParams()
        {
            SetLabelText(durationlbl, FormatTime(bx.Duration));
            int value = GetTrackbarValue(tempoControl);
            if (value >= 0)
            {
                bx.Tempo = value;
            }

            value = GetTrackbarValue(transposeControl);
            if (value >= 0)
            {
                bx.Transpose = value;
            }
            value = GetComboBoxIndex(reverbcb);
            if (value >= 0)
            {
                bx.ReverbType = (value + 1);
            }
            value = GetTrackbarValue(volumeControl);
            if (value >= 0)
            {
                bx.Volume = value;
            }
            foreach (Control c in midichpnl.Controls)
            {
                if (c is CheckBox cb)
                {
                    short midich = (short)Convert.ToInt16(cb.Name.Split('_')[1]);
                    bool muted = !cb.Checked;
                    bx.MuteChannel(midich, muted);
                }
            }
        }

        private void Stopbut_Click(object sender, EventArgs e)
        {
            bx.Stop();
            SetControlVisiblity(mainControlPanel, false);
            SetLabelText(durationlbl, "");
            SetLabelText(progresslbl, "");
            SetLabelText(statustitle, "");
            SetTrackbarValue(seekbar, 0, 0);
        }

        private void Seekbar_ValueChanged(object sender, EventArgs e)
        {
            if (seekbar_held)
            {
                SetLabelText(seekpos, FormatTime(seekbar.Value));
            }
        }

        private void Midich_muteall_btn_Click(object sender, EventArgs e)
        {
            foreach (Control c in midichpnl.Controls)
            {
                if (c is CheckBox cb)
                {
                    SetCheckBoxChecked(cb, false);
                }
            }
        }

        private void Midich_muteinvert_btn_Click(object sender, EventArgs e)
        {
            foreach (Control c in midichpnl.Controls)
            {
                if (c is CheckBox cb)
                {
                    SetCheckBoxChecked(cb, !cb.Checked);
                }
            }
        }

        private void Midichrstbtn_Click(object sender, EventArgs e)
        {
            foreach (Control c in midichpnl.Controls)
            {
                if (c is CheckBox cb)
                {
                    SetCheckBoxChecked(cb, true);
                }
            }
        }

        private void Transposerstbtn_Click(object sender, EventArgs e)
        {
            SetTrackbarValue(transposeControl, 0);
            SetTranspose(0);
        }


        private void Transposetb_Scroll(object sender, EventArgs e)
        {
            SetTranspose(transposeControl.Value);
        }

        private void Patchswlnchr_Click(object sender, EventArgs e)
        {
            try
            {
                string serialized_data;
                if (bx.active)
                {
                    serialized_data = SerializeData();
                }
                else
                {
                    serialized_data = SerializeData(false);
                }

                ProcessStartInfo startInfo = new ProcessStartInfo(cwd + _patchswitcher_exe)
                {
                    Arguments = serialized_data
                };
                Debug.WriteLine("Sending Session Data: " + serialized_data);
                Process.Start(startInfo);
                Application.Exit();
            }
            catch (Exception f)
            {
                DialogResult errormsg = MessageBox.Show("There was an error launching the Patch Switcher\n\n" + f.Message, "Error", MessageBoxButtons.RetryCancel, MessageBoxIcon.Error);
                if (errormsg == DialogResult.Retry)
                {
                    Patchswlnchr_Click(sender, e);
                }
            }
        }

        private void BeatnikLogo_Click(object sender, EventArgs e)
        {
            bx.AboutBox();
        }

        private void OpenFile_Click(object sender, EventArgs e)
        {
            string file = ZefieLib.Prompts.BrowseOpenFile("Open MIDI File", null, "All Supported Files (*.mid;*.kar;*.rmf;*.wav;*.aif;*.aiff;*.au)|*.mid;*.kar;*.rmf;*.wav;*.aif;*.aiff;*.au|MIDI Files (*.mid;*.kar)|*.mid;*.kar|Beatnik Files (*.rmf)|*.rmf|WAV Files (*.wav)|*.wav|AIF Files (*.aif;*.aiff)|*.aif;*.aiff|AU Files (*.au)|*.au|All files (*.*)|*.*");
            if (file.Length > 0)
            {
                if (File.Exists(file))
                {
                    SetLabelText(statustitle, "");
                    SetLabelText(statusfile, "");
                    SetLabelText(progresslbl, "");
                    SetLabelText(durationlbl, "");
                    current_file = file;
                    SetVolume(volumeControl.Value);
                    PlayFile(file, loopcb.Checked);
                }
            }
        }

        private string GetBXSafeFilename(string file)
        {
            string fileext = Path.GetExtension(file).ToLower();
            file = Path.GetFileNameWithoutExtension(file) + fileext;
            // returns new filename and if it was replaced
            if (fileext == ".kar")
            {
                file = Path.GetFileNameWithoutExtension(file) + ".mid";
            }
            Regex rgx = new Regex("[^a-zA-Z0-9_() -.]");
            return rgx.Replace(file, "");
        }

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern long DeleteUrlCacheEntry(string lpszUrlName);



        private bool GetBXFilenameWasAltered(string filename, string simulated_filename)
        {
            return !(simulated_filename == Path.GetFileNameWithoutExtension(filename));
        }

        private void PlayFile(string file, bool loop = false)
        {
            SetLabelText(statustitle, "");
            SetButtonEnabled(infobut, false);
            current_file = file;
            string bxchk = GetBXSafeFilename(file);
            bool needs_minihttp = GetBXFilenameWasAltered(file, Path.GetFileNameWithoutExtension(bxchk));

            if (Path.GetExtension(file).ToLower() == ".kar" || needs_minihttp)
            {
                string simulated_filename = ZefieLib.Strings.GenerateString(64) + Path.GetExtension(bxchk);
                // hack to send .kar and other unsupported filenames as midi without modifying local filesystem
                if (needs_minihttp)
                {
                    Debug.WriteLine(file + " name unsupported by Beatnik, miniHTTP required. Simulated Filename: " + simulated_filename);
                }

                Debug.WriteLine("trying to load file via miniHTTP");

                PlayFileViaMiniHTTP(simulated_filename, loop);
            }
            else
            {
                bx.PlayFile(file, loopcb.Checked);
            }
        }

        private void PlayFile(Stream filedata, string filename, bool loop = false)
        {
            current_file = filename;
            current_datastream = filedata;
            SetLabelText(statustitle, "");
            SetButtonEnabled(infobut, false);
            PlayFileViaMiniHTTP(filename, loop);
            Debug.WriteLine("trying to load internal memory data (as " + filename + ")");
        }

        private void PlayFileViaMiniHTTP(string simulated_filename, bool loop)
        {
            if (tcp == null)
            {
                Debug.WriteLine("zefie minihttp starting up");
                while (!ZefieLib.Networking.IsPortAvailable(http_port, IPAddress.Loopback))
                {
                    http_port--;
                }
                Debug.WriteLine("zefie minihttp found available port on localhost:" + http_port);
                StartHTTPServer();
            }
            using (BackgroundWorker bxrequest = new BackgroundWorker())
            {
                bxrequest.DoWork += new DoWorkEventHandler(
                delegate (object o1, DoWorkEventArgs arg1)
                {
                    try
                    {
                        while (!http_ready)
                        {
                            Thread.Sleep(100);
                        }
                        bx.PlayFile("http://127.0.0.1:" + http_port.ToString() + "/" + simulated_filename, loop, current_file);
                    }
                    catch { }
                    GC.Collect();
                }
                );
                bxrequest.RunWorkerAsync();
            }
        }

        private string GetMimeType(string filename)
        {
            switch (Path.GetExtension(filename).ToLower())
            {
                case ".mid":
                case ".kar":
                case ".midi":
                    return "audio/midi";

                case ".rmf":
                    return "audio/rmf";

                case ".wav":
                    return "audio/wav";

                case ".aif":
                case ".aiff":
                    return "audio/aiff";

                case ".au":
                    return "audio/basic";

                default:
                    return "application/octet-stream";
            }
        }

        private void StartHTTPServer()
        {
            using (BackgroundWorker minihttp = new BackgroundWorker())
            {
                minihttp.DoWork += new DoWorkEventHandler(
                    delegate (object o, DoWorkEventArgs arg)
                    {
                        while (true)
                        {
                            try
                            {
                                Socket sock;
                                if (tcp == null)
                                {
                                    tcp = new TcpListener(new System.Net.IPAddress(16777343), http_port);
                                    tcp.Start();
                                    Debug.WriteLine("minihttp listening on port " + http_port);
                                    http_ready = true;
                                }
                                Debug.WriteLine("minihttp ready for request");
                                sock = tcp.AcceptSocket();

                                Debug.WriteLine("minihttp responding to request");
                                byte[] readbyte = new byte[4096];

                                sock.Receive(readbyte, SocketFlags.None);
                                string request = Encoding.UTF8.GetString(readbyte);
                                //Debug.WriteLine("minihttp client request headers:\n"+request);
                                string request_file = request.Split('\r')[0].Split('/')[1].Split(' ')[0];
                                string mimetype = GetMimeType(request_file);
                                Stream fs = null;
                                Debug.WriteLine("minihttp client requested \"" + request_file + "\", sending as " + mimetype);
                                string last_modified = DateTime.UtcNow.ToString("r");
                                if (current_datastream != null)
                                {
                                    Debug.WriteLine("minihttp found memory data");
                                    fs = current_datastream;
                                }
                                else
                                {
                                    Debug.WriteLine("minihttp opening file " + current_file);
                                    fs = File.OpenRead(current_file);
                                    last_modified = new FileInfo(current_file).LastAccessTimeUtc.ToString("r");
                                }

                                byte[] httpheaders = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n" +
                                   "Date: " + DateTime.UtcNow.ToString("r") + "\r\n" +
                                   "Server: Zefie's MiniHTTP Simulator\r\n" +
                                   "Content-Type: " + mimetype + "\r\n" +
                                   "Content-Length: " + fs.Length + "\r\n" +
                                   "Last-Modified: " + last_modified + "\r\n" +
                                   "Cache-Control: no-cache" + "\r\n" +
                                   "Expires: -1\r\n" +
                                   "Connection: close\r\n\r\n");
                                sock.Send(httpheaders);
                                //Debug.WriteLine("minihttp server response headers:\n"+Encoding.UTF8.GetString(httpheaders));
                                readbyte = new byte[4096];
                                while (fs.Read(readbyte, 0, 4096) > 0)
                                {
                                    sock.Send(readbyte);
                                }

                                // give player time to buffer
                                Thread.Sleep(100);

                                // clean up

                                if (current_datastream != null)
                                {
                                    Debug.WriteLine("minihttp releasing memory data");
                                    current_datastream.Close();
                                    current_datastream.Dispose();
                                    current_datastream = null;
                                }
                                fs.Close();
                                fs.Dispose();
                                fs = null;
                                if (sock.Connected)
                                {
                                    Debug.WriteLine("minihttp disconnecting socket");
                                    try { sock.Disconnect(false); }
                                    catch (SocketException e) { Debug.WriteLine(e.Message); }
                                }
                                else
                                {
                                    Debug.WriteLine("minihttp client already disconneceted socket");
                                }
                                sock.Shutdown(SocketShutdown.Both);
                                sock.Dispose();
                                sock = null;
    
                            }
                            catch (Exception e) { Debug.WriteLine(e.Message); }
                            GC.Collect();
                        }
                    });
                minihttp.RunWorkerAsync();
            }
        }

        private void MidiChannel_toggle(object sender, EventArgs e)
        {
            CheckBox thebox = (CheckBox)sender;
            short midich = (short)Convert.ToInt16(thebox.Name.Split('_')[1]);
            bool muted = !thebox.Checked;
            bx.MuteChannel(midich, muted);
        }

        private void Infobut_Click(object sender, EventArgs e)
        {
            bx.DoMenuItem("Copyright");
        }

        private void Reverbcb_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (settingReverbCB)
            {
                settingReverbCB = false;
            }
            else
            {
                bx.ReverbType = (((ComboBox)sender).SelectedIndex + 1);
            }
        }

        private void Progresslbl_Click(object sender, EventArgs e)
        {
            bx.Position = 0;
        }

        private void BXPlayerGUI_DragDrop(object sender, DragEventArgs e)
        {
            string[] s = (string[])e.Data.GetData(DataFormats.FileDrop, false);
            if (s.Length > 0) {
                if (CheckExtensionSupported(s[0]))
                {
                    PlayFile(s[0], loopcb.Checked);
                }
            }
        }

        private bool CheckExtensionSupported(string filename)
        {
            string ext = Path.GetExtension(filename).ToLower();
            switch (ext)
            {
                case ".mid":
                case ".midi":
                case ".kar":
                case ".rmf":
                case ".wav":
                case ".aif":
                case ".aiff":
                case ".au":
                return true;
            }
            return false;
        }

        private void BXPlayerGUI_DragEnter(object sender, DragEventArgs e)
        {

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] s = (string[])e.Data.GetData(DataFormats.FileDrop, false);
                if (s.Length > 0)
                {
                    if (CheckExtensionSupported(s[0]))
                    {
                        e.Effect = DragDropEffects.Link;
                    }
                    else
                    {
                        e.Effect = DragDropEffects.None;
                    }
                }
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }

        private void Form1_FormClosed(object sender, FormClosedEventArgs e)
        {
            if (_mutexApplication != null)
            {
                _mutexApplication.Dispose();
            }
        }
    }
    public class NamedPipeXmlPayload
    {
        /// <summary>
        ///     A list of command line arguments.
        /// </summary>
        [XmlElement("CommandLineArguments")]
        public List<string> CommandLineArguments { get; set; } = new List<string>();
    }
}
