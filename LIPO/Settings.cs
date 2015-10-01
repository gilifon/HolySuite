using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LIPO
{
    public partial class Settings : Form
    {
        public string StationName { get { return TB_Name.Text; } set { TB_Name.Text = value; } }
        public string StationIP { get { return TB_IP.Text; } set { TB_IP.Text = value; } }

        public Settings()
        {
            InitializeComponent();
        }

        public Settings(string Name, String IP)
        {
            InitializeComponent();
            this.StationName = Name;
            this.StationIP = IP;
        }

        private void Btn_OK_Click(object sender, EventArgs e)
        {
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }
}
