﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Karabina.SharePoint.Provisioning
{
    public partial class SelectSharePoint : Form
    {
        private SharePointVersion _versionSelected = SharePointVersion.SharePoint_Invalid;
        public SharePointVersion VersionSelected
        {
            get { return _versionSelected; }
            set { _versionSelected = value; }
        }

        public delegate void SetStatusTextDelegate(string message);

        public SetStatusTextDelegate SetStatusBarText;

        public SelectSharePoint()
        {
            InitializeComponent();          

        }

        public void setTopic(string topic)
        {
            lTopic.Text = topic;

        }

        private void bOkay_Click(object sender, EventArgs e)
        {
            Close();

        }

        private void bCancel_Click(object sender, EventArgs e)
        {
            Close();

        }

        private void SetVersionSelected(object sender, EventArgs e)
        {
            RadioButton rb = sender as RadioButton;
            string tag = (sender as Control).Tag.ToString();
            int verNum = Convert.ToInt32(tag.Replace("Version0", ""));
            _versionSelected = (SharePointVersion)verNum;

        }

        private void SetStatusText(object sender, EventArgs e)
        {
            string tag = Constants.Version00;
            tag = (sender as Control).Tag.ToString();
            SetStatusBarText(Properties.Resources.ResourceManager.GetString(tag));

        }

        private void SetStatusDefault(object sender, EventArgs e)
        {
            SetStatusBarText(Properties.Resources.ResourceManager.GetString(Constants.Version00));

        }



    }

}
