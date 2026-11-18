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

      this.Text = "Prompting UI";
      this.ClientSize = new Size(1100, 760);
      this.MinimumSize = new Size(640, 400);
      this.StartPosition = FormStartPosition.CenterScreen;
      this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

      this.Controls.Add(_WebView);

      this.Load += this.OnFormLoad;

      this.UpdateTitle();
      Program.ProfileManager.ActiveProfileChanged += this.OnActiveProfileChanged;

    }

    /// <summary>
    /// Shows the active profile within the window title (PRF-10).
    /// </summary>
    private void UpdateTitle() {

      this.Text = "Prompting UI - " + Program.ProfileManager.ActiveProfileDisplayName;

    }

    private void OnActiveProfileChanged(object sender, EventArgs e) {

      //profile switches are triggered by UJMW calls (non-UI thread)
      if (this.IsHandleCreated && this.InvokeRequired) {
        this.BeginInvoke(new Action(this.UpdateTitle));
      }
      else {
        this.UpdateTitle();
      }

    }

    protected override void Dispose(bool disposing) {

      if (disposing) {
        Program.ProfileManager.ActiveProfileChanged -= this.OnActiveProfileChanged;
      }

      base.Dispose(disposing);

    }

    /// <summary>
    /// Attaches the WebView2 control to the in-memory ASP.NET Core engine.
    /// </summary>
    private void OnFormLoad(object sender, EventArgs e) {

      Program.AspApplication.Attach(_WebView, "/");

    }

  }

}
