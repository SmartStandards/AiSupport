using Microsoft.Web.WebView2.WinForms;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace AI.SmartStandards.ChatClient {

  /// <summary>
  /// Main window which only contains the WebView2 control.
  /// </summary>
  public class FormMain : Form {

    private readonly WebView2 _WebView;

    public FormMain() {

      _WebView = new WebView2();
      _WebView.Dock = DockStyle.Fill;
      _WebView.DefaultBackgroundColor = Color.White;

      this.Text = "SmartStandards - Prompting UI";
      this.ClientSize = new Size(1100, 760);
      this.MinimumSize = new Size(640, 400);
      this.StartPosition = FormStartPosition.CenterScreen;
      this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

      this.Controls.Add(_WebView);

      this.Load += this.OnFormLoad;

    }

    /// <summary>
    /// Attaches the WebView2 control to the in-memory ASP.NET Core engine.
    /// </summary>
    private void OnFormLoad(object sender, EventArgs e) {

      Program.AspApplication.Attach(_WebView, "/");

    }

  }

}
