using AI.SmartStandards.InteractivePrompting;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace AI.SmartStandards.ChatClient {

  /// <summary>
  /// Small start dialog to select the harness profile (PRF-6).
  /// </summary>
  public class FormProfileSelection : Form {

    private readonly ListBox _ProfileList;

    private readonly HarnessProfileInfo[] _Profiles;

    public FormProfileSelection(HarnessProfileInfo[] profiles) {

      _Profiles = profiles;

      this.Text = "Prompting UI - Select profile";
      this.ClientSize = new Size(360, 260);
      this.FormBorderStyle = FormBorderStyle.FixedDialog;
      this.MaximizeBox = false;
      this.MinimizeBox = false;
      this.StartPosition = FormStartPosition.CenterScreen;

      _ProfileList = new ListBox();
      _ProfileList.SetBounds(12, 12, 336, 196);
      _ProfileList.DoubleClick += this.OnProfileListDoubleClick;

      foreach (HarnessProfileInfo profile in profiles) {

        int index = _ProfileList.Items.Add(profile.DisplayName);

        if (profile.IsActive) {
          _ProfileList.SelectedIndex = index;
        }

      }

      if (_ProfileList.SelectedIndex < 0 && _ProfileList.Items.Count > 0) {
        _ProfileList.SelectedIndex = 0;
      }

      Button okButton = new Button();
      okButton.Text = "Start";
      okButton.SetBounds(192, 220, 75, 28);
      okButton.DialogResult = DialogResult.OK;

      Button cancelButton = new Button();
      cancelButton.Text = "Cancel";
      cancelButton.SetBounds(273, 220, 75, 28);
      cancelButton.DialogResult = DialogResult.Cancel;

      this.AcceptButton = okButton;
      this.CancelButton = cancelButton;

      this.Controls.Add(_ProfileList);
      this.Controls.Add(okButton);
      this.Controls.Add(cancelButton);

    }

    /// <summary>
    /// Id of the selected profile.
    /// </summary>
    public string SelectedProfileId {
      get {

        if (_ProfileList.SelectedIndex < 0) {
          return string.Empty;
        }

        return _Profiles[_ProfileList.SelectedIndex].ProfileId;
      }
    }

    private void OnProfileListDoubleClick(object sender, EventArgs e) {

      if (_ProfileList.SelectedIndex >= 0) {
        this.DialogResult = DialogResult.OK;
        this.Close();
      }

    }

  }

}
