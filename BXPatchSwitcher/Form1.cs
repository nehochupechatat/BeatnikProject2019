﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using System.Xml;

namespace BXPatchSwitcher
{
    public partial class Form1 : Form
    {
        private readonly string cwd;
        private readonly string bxpatch_default_dest = Environment.GetEnvironmentVariable("WINDIR") + "\\patches.hsb";
        private readonly string bxpatch_preferred_dest;
        private readonly string[] args = Environment.GetCommandLineArgs();
        private readonly string patches_dir;
        private readonly string bankfile;
        private string bxpatch_dest;
        private bool junctioned = false;
        private int default_index;
        private string[] options;
        private string return_exe;
        private string current_hash;

        public Form1()
        {
            InitializeComponent();
#if DEBUG
            cwd = "C:\\bin\\BeatnikProject2019";
#else
            cwd = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName) + "\\";
#endif
            patches_dir = cwd + "BXBanks\\";
            bankfile = patches_dir + "BXBanks.xml";
            bxpatch_preferred_dest = cwd + "\\patches.hsb";
            bxpatch_dest = bxpatch_default_dest;
        }

        private void BxpatchBtn_Click(object sender, EventArgs e)
        {
            // store selected patch
            int patchidx = bxpatchcb.SelectedIndex;

            string rawopts = patchidx.ToString() + " " + junctionchk.Checked.ToString();
            string outopts = "";

            // for handling session data from BXPlayerGUI

            if (options != null)
            {
                List<string> list;
                list = new List<string>(options);
                list.RemoveAt(0);
                rawopts += " " + ZefieLib.Data.Base64Encode(String.Join("|", options));
                outopts = ZefieLib.Data.Base64Encode(String.Join("|", list.ToArray()));
                Debug.WriteLine("Received Session Data: " + rawopts.Split(' ')[2]);
                Debug.WriteLine("Return Session Data: " + outopts);
            }
            string res = InstallPatch(patchidx, outopts);
            if (res == "NEEDADMIN")
            {
                if (ZefieLib.UAC.IsAdmin)
                {
                    res = InstallPatch(patchidx, outopts);
                }
                else if (ZefieLib.UAC.RunAsAdministrator(rawopts))
                {
                    Application.Exit();
                }
            }
            if (res == "EXIT")
            {
                Application.Exit();
            }
            else if (res != "OK" && res != "NEEDADMIN")
            {
                MessageBox.Show(res, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

        }

        private string InstallPatch(int patchidx, string outopts)
        {
            try
            {
                string source_file = GetHSBFileByIndex(patchidx);
                if (File.Exists(bxpatch_dest))
                {
                    File.SetAttributes(bxpatch_dest, FileAttributes.Normal);
                }
                if (junctionchk.Checked) {
                    if (File.Exists(bxpatch_preferred_dest)) {
                        File.SetAttributes(bxpatch_preferred_dest, FileAttributes.Normal);
                    }
                    File.Copy(source_file, bxpatch_preferred_dest, true);
                    if (File.Exists(bxpatch_default_dest))
                    {                        
                        bxpatch_dest = bxpatch_preferred_dest;
                        junctioned = true;
                    }
                    else
                    {
                        if (ZefieLib.UAC.IsAdmin)
                        {
                            ZefieLib.Path.CreateSymbolicLink(bxpatch_default_dest, bxpatch_preferred_dest, ZefieLib.Path.SymbolicLink.File);
                            junctioned = true;
                        } 
                        else
                        {
                            return "NEEDADMIN";
                        }
                    }
                    if (!File.Exists(bxpatch_preferred_dest))
                    {
                        return "Unknown error when copying file from \n\"" + source_file + "\"\nto\n"+ bxpatch_preferred_dest;
                    }
                }
                else
                {
                    if (File.Exists(bxpatch_dest))
                    {
                        File.Delete(bxpatch_dest);
                    }
                    if (!junctionchk.Checked && junctioned)
                    {
                        File.Delete(bxpatch_default_dest);
                        bxpatch_dest = bxpatch_default_dest;
                        junctioned = false;
                    }
                    File.Copy(source_file, bxpatch_dest);
                }


                File.SetAttributes(bxpatch_dest, FileAttributes.ReadOnly);
                FileSecurity fSecurity = File.GetAccessControl(bxpatch_dest);

                // Add the FileSystemAccessRule to the security settings.
                fSecurity.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl, AccessControlType.Allow));

                // Set the new access settings.
                File.SetAccessControl(bxpatch_dest, fSecurity);
                if (!junctioned)
                {
                    if (File.Exists(bxpatch_preferred_dest))
                    {
                        File.Delete(bxpatch_preferred_dest);
                    }
                }

                if (return_exe != null)
                {
                    DialogResult result = MessageBox.Show("Successfully installed patchset!\n\nWould you like to run the BeatnikX Player now?", "Success", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                    if (result == DialogResult.Yes)
                    {
                        ProcessStartInfo startInfo = new ProcessStartInfo(return_exe)
                        {
                            Arguments = outopts
                        };
                        Process.Start(startInfo);
                        return "EXIT";
                    }
                    Init_Form();
                    return "OK";
                }
                else
                {
                    MessageBox.Show("Successfully installed patchset!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2);
                    Init_Form();
                    return "OK";
                }
            }
            catch (Exception f)
            {
                if (f.GetType().ToString() != "System.UnauthorizedAccessException")
                {
                    MessageBox.Show("Error (" + f.GetType().ToString() + "):\n\n" + f.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return f.Message;
            }
        }

        private string GetHSBFileByIndex(int index)
        {
            if (File.Exists(bankfile))
            {
                using (XmlReader reader = XmlReader.Create(bankfile))
                {
                    int count = 0;
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            if (reader.Name == "bank")
                            {
                                if (count == index)
                                {
                                    string patchfile = patches_dir + reader.GetAttribute("src");
                                    return patchfile;
                                }
                                else
                                {
                                    count++;
                                    continue;
                                }
                            }
                        }
                    }

                }
            }
            return "";
        }

        private void Init_Form()
        {
            if (File.Exists(bxpatch_preferred_dest) && File.Exists(bxpatch_default_dest))
            {
                if (ZefieLib.Cryptography.Hash.SHA1(bxpatch_preferred_dest) == ZefieLib.Cryptography.Hash.SHA1(bxpatch_default_dest))
                {
                    bxpatch_dest = bxpatch_preferred_dest;
                    junctioned = true;
                }
                junctionchk.Checked = junctioned;
                if (File.Exists(bxpatch_default_dest))
                {
                    try
                    {
                        current_hash = ZefieLib.Cryptography.Hash.SHA1(bxpatch_default_dest);
                        Debug.WriteLine("Current Patches Hash: " + current_hash);
                    }
                    catch
                    {
                        junctionchk.Checked = true;
                        bxinsthsb.Text = "~ CANNOT READ, BROKEN JUNCTION ~";
                        Debug.WriteLine("Could not read " + bxpatch_default_dest + ", bad junction?");
                    }
                }
            }
            else
            {
                Debug.WriteLine("WARN: No patches installed!");
                bxinsthsb.Text = "None";
            }


            if (File.Exists(bankfile))
            {
                bxpatchcb.Items.Clear();
                int idx = 0;
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
                                string patchsha1_expected = reader.GetAttribute("sha1").ToLower();
                                string @default = reader.GetAttribute("default");
                                if (@default != null)
                                {
                                    if (Convert.ToBoolean(@default))
                                    {
                                        default_index = idx;
                                        Debug.WriteLine("Detected " + patchname + " as currently perferred default");
                                    }
                                }
                                if (File.Exists(patchfile))
                                {
                                    string patchsha1 = ZefieLib.Cryptography.Hash.SHA1(patchfile);
                                    if (patchsha1 == patchsha1_expected)
                                    {
                                        Debug.WriteLine("Found " + patchname + "(SHA1: " + patchsha1 + ", OK)");
                                        bxpatchcb.Items.Add(patchname);
                                        if (patchsha1 == current_hash)
                                        {
                                            Debug.WriteLine("Detected " + patchname + " as currently installed");
                                            bxinsthsb.Text = patchname;
                                            if (junctioned)
                                            {
                                                bxinsthsb.Text += " (Junctioned)";
                                            }
                                            bxpatchcb.SelectedIndex = bxpatchcb.Items.Count - 1;
                                        }
                                    }
                                    else
                                    {
                                        Debug.WriteLine("Found " + patchname + "(SHA1: " + patchsha1 + ", BAD)");
                                    }
                                }
                                else
                                {
                                    Debug.WriteLine("Could not find " + patchfile);
                                }
                                idx++;
                            }
                        }
                    }
                }
                if (bxinsthsb.Text == "Unknown" || bxinsthsb.Text == "None" || bxinsthsb.Text.Substring(0, 1) == "~")
                {
                    bxpatchcb.SelectedIndex = default_index;
                }
            }
            else
            {
                MessageBox.Show("Could not open " + bankfile, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
            }
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            Init_Form();
            bool has_index = false;
            if (args.Length > 1)
            {
                int argidx = -1;
                try { argidx = Convert.ToInt32(args[1]); }
                catch { }
                if (argidx >= 0)
                {
                    bxpatchcb.SelectedIndex = argidx;
                    has_index = true;
                }
                if (args.Length > 3)
                {
                    junctionchk.Checked = Convert.ToBoolean(args[2]);
                    options = Encoding.UTF8.GetString(ZefieLib.Data.Base64Decode(args[3])).Split('|');
                    if (File.Exists(options[0]))
                    {
                        return_exe = options[0];
                    }
                }
                if (args.Length > 2 || !has_index)
                {
                    if (has_index)
                    {
                        junctionchk.Checked = Convert.ToBoolean(args[2]);
                    }
                    else
                    {
                        try
                        {
                            int argidx2 = has_index ? 2 : 1;

                            options = Encoding.UTF8.GetString(ZefieLib.Data.Base64Decode(args[argidx2])).Split('|');
                            if (File.Exists(options[0]))
                            {
                                return_exe = options[0];
                            }
                        }
                        catch { }
                    }
                }
                if (has_index)
                {
                    BxpatchBtn_Click(null, null);
                }
            }
        }
    }
}
