using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace DolatMan.Models;

public sealed class Applicant : INotifyPropertyChanged
{
    private string _fullName = "";
    private string _mobile = "";
    private string _uniqueId = "";
    private string _fidaCode = "";
    private string _birthDate = "";
    private int _mappingIndex;
    private string _queueStatus = "آماده";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FullName { get => _fullName; set => Set(ref _fullName, value); }
    public string Mobile { get => _mobile; set => Set(ref _mobile, value); }
    public string UniqueId { get => _uniqueId; set => Set(ref _uniqueId, value); }
    public string FidaCode { get => _fidaCode; set => Set(ref _fidaCode, value); }
    // Enter as روز/ماه/سال, using the same calendar and values requested by the portal.
    public string BirthDate { get => _birthDate; set => Set(ref _birthDate, value); }
    // 0: unique/fida, 1: fida/unique, 2: fida/fida. Operator selects one explicitly.
    public int MappingIndex
    {
        get => _mappingIndex;
        set
        {
            if (Set(ref _mappingIndex, value)) OnPropertyChanged(nameof(MappingLabel));
        }
    }


    // Last queue state is stored with the applicant so completed items are not submitted again on the next run.
    public string QueueStatus
    {
        get => _queueStatus;
        set => Set(ref _queueStatus, value);
    }

    [JsonIgnore]
    public string MappingLabel => MappingIndex switch
    {
        0 => "شناسه یکتا / کد فیدا",
        1 => "کد فیدا / شناسه یکتا",
        2 => "کد فیدا / کد فیدا",
        _ => "انتخاب نشده"
    };

    [JsonIgnore]
    public string SafeFolderName
    {
        get
        {
            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string(FullName.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            return string.IsNullOrWhiteSpace(safe) ? "متقاضی" : safe;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
