using System.Diagnostics;
using System.Text.RegularExpressions;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: CompareVideos.exe video1.mp4 video2.mp4");
    return 1;
}

string file1 = args[0];
string file2 = args[1];

if (!File.Exists(file1) || !File.Exists(file2))
{
    Console.Error.WriteLine("One or both files doesn't exists");
    return 1;
}

if (!IsToolAvailable("ffmpeg") || !IsToolAvailable("ffprobe"))
{
    Console.Error.WriteLine("Error: ffmpeg and/or ffprobe are not available on PATH or in the application folder.");
    Console.Error.WriteLine("Make sure that ffmpeg.exe and ffprobe.exe are installed and accessible.");
    return 1;
}


string trimmed1 = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".mp4");
string trimmed2 = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".mp4");

double duration1 = GetDuration(file1);
double duration2 = GetDuration(file2);
double minDuration = Math.Min(duration1, duration2) - 4;

(int w1, int h1) = GetResolution(file1);
(int w2, int h2) = GetResolution(file2);

if (w1 != w2 || h1 != h2)
{
    Console.WriteLine($"Detected resolutions: {w1}x{h1} vs {w2}x{h2}");
    string better = (w1 * h1) > (w2 * h2) ? file1 : file2;
    Console.WriteLine($"\nAdvise: Video file '{Path.GetFileName(better)}' contains major resolution");
    return 0;
}

Console.WriteLine("Cutting videos...");
RunProcess("ffmpeg", $"-y -ss 2 -t {minDuration} -i \"{file1}\" -an -c:v libx264 \"{trimmed1}\"", true);
RunProcess("ffmpeg", $"-y -ss 2 -t {minDuration} -i \"{file2}\" -an -c:v libx264 \"{trimmed2}\"", true);

Console.WriteLine("Processing SSIM...");
string ssimOutput = RunProcess("ffmpeg", $"-i \"{trimmed1}\" -i \"{trimmed2}\" -lavfi \"[0:v][1:v]ssim\" -f null -", true);
double ssimScore = ExtractSSIM(ssimOutput);

Console.WriteLine("Processing PSNR...");
string psnrOutput = RunProcess("ffmpeg", $"-i \"{trimmed1}\" -i \"{trimmed2}\" -lavfi \"[0:v][1:v]psnr\" -f null -", true);
double psnrScore = ExtractPSNR(psnrOutput);

long size1 = new FileInfo(file1).Length;
long size2 = new FileInfo(file2).Length;

string codec1 = GetCodec(file1);
string codec2 = GetCodec(file2);

Console.WriteLine("\n=== RESULTS ===");
Console.WriteLine($"SSIM: {ssimScore:F4}");
Console.WriteLine($"PSNR: {psnrScore:F2} dB");
Console.WriteLine($"Size: {Path.GetFileName(file1)} = {size1 / 1e6:F2} MB");
Console.WriteLine($"Size: {Path.GetFileName(file2)} = {size2 / 1e6:F2} MB");
Console.WriteLine($"Codec: {codec1} vs {codec2}");

string result = Decide(file1, file2, ssimScore, psnrScore, codec1, codec2, size1, size2);
Console.WriteLine($"\nAdvise: Video file \"{Path.GetFileName(result)}\" has better c");

File.Delete(trimmed1);
File.Delete(trimmed2);

return 0;

static bool IsToolAvailable(string tool)
{
    try
    {
        var whereProc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "where",
                Arguments = tool,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        whereProc.Start();
        string path = whereProc.StandardOutput.ReadLine();
        whereProc.WaitForExit();

        if (!string.IsNullOrWhiteSpace(path))
        {
            Console.WriteLine($"{tool} detected at path: {path}");
            return true;
        }
        else
        {
            return false;
        }
    }
    catch
    {
        return false;
    }
}

static string RunProcess(string exe, string args, bool captureOutput = false)
{
    var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput,
            UseShellExecute = false,
            CreateNoWindow = true
        }
    };
    process.Start();
    string output = captureOutput ? process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd() : "";
    process.WaitForExit();
    return output;
}

static (int width, int height) GetResolution(string file)
{
    string output = RunProcess("ffprobe", $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0:s=x \"{file}\"", true);
    string[] parts = output.Trim().Split('x');
    return (int.Parse(parts[0]), int.Parse(parts[1]));
}

static double GetDuration(string file)
{
    string output = RunProcess("ffprobe", $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{file}\"", true);
    return double.TryParse(output.Trim(), out double dur) ? dur : 0;
}

static string GetCodec(string file)
{
    string output = RunProcess("ffprobe", $"-v error -select_streams v:0 -show_entries stream=codec_name -of default=nk=1:nw=1 \"{file}\"", true);
    return output.Trim();
}

static double ExtractSSIM(string output)
{
    var match = Regex.Match(output, @"All:(\d\.\d+)");
    return match.Success ? double.Parse(match.Groups[1].Value) : 0;
}

static double ExtractPSNR(string output)
{
    var match = Regex.Match(output, @"average:([\d\.]+)");
    return match.Success ? double.Parse(match.Groups[1].Value) : 0;
}

static string Decide(string file1, string file2, double ssim, double psnr, string codec1, string codec2, long size1, long size2)
{
    if (ssim > 0.98 && psnr > 35)
    {
        if (codec1 == codec2)
        {
            return size1 <= size2 ? file1 : file2;
        }
        else
        {
            int priority1 = CodecPriority(codec1);
            int priority2 = CodecPriority(codec2);
            return priority1 >= priority2 ? file1 : file2;
        }
    }
    else
    {
        return ssim >= 0.99 ? file1 : file2;
    }
}

static int CodecPriority(string codec)
{
    return codec switch
    {
        "hevc" => 3,
        "vp9" => 3,
        "av1" => 4,
        "h264" => 2,
        _ => 1
    };
}