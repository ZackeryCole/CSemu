using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Aeon.Emulator.DebugSupport;
using Aeon.Emulator.Dos.Programs;
using Aeon.Emulator.RuntimeExceptions;

namespace CSemu.Emulator
{
    /// <summary>
    /// Hosts a <see cref="Aeon.Emulator.VirtualMachine"/> instance and provides additional services.
    /// </summary>
    public sealed class EmulatorHost : IDisposable
    {
        private Thread processorThread;
        private volatile EmulatorState targetState;
        private readonly AutoResetEvent resumeEvent = new AutoResetEvent(false);
        private readonly ConcurrentQueue<MouseEvent> mouseQueue = new ConcurrentQueue<MouseEvent>();
        private EmulatorState currentState;
        private bool disposed;
        private long totalInstructions;
        private readonly SortedSet<Keys> keysPresssed = new SortedSet<Keys>();
        private int emulationSpeed = 10_000_000;
        private readonly InstructionLog log;

        /// <summary>
        /// The smallest number that may be assigned to the EmulationSpeed property.
        /// </summary>
        public const int MinimumSpeed = 1_000_000;

        /// <summary>
        /// Initializes a new instance of the EmulatorHost class.
        /// </summary>
        public EmulatorHost()
            : this(FastVirtualMachine.Create(), null)
        {
        }
        public EmulatorHost(int physicalMemory)
            : this(FastVirtualMachine.Create(physicalMemory), null)
        {
        }
        /// <summary>
        /// Initializes a new instance of the EmulatorHost class.
        /// </summary>
        /// <param name="instructionLog">Log which will be used to record instructions.</param>
        public EmulatorHost(InstructionLog instructionLog)
            : this(FastVirtualMachine.Create(), instructionLog)
        {
        }
        /// <summary>
        /// Initializes a new instance of the EmulatorHost class.
        /// </summary>
        /// <param name="virtualMachine">VirtualMachine instance to host.</param>
        /// <param name="instructionLog">Log which will be used to record instructions.</param>
        public EmulatorHost(VirtualMachine virtualMachine, InstructionLog instructionLog)
        {
            VirtualMachine = virtualMachine ?? throw new ArgumentNullException(nameof(virtualMachine));
            VirtualMachine.VideoModeChanged += (s, e) => this.OnVideoModeChanged(e);
            VirtualMachine.MouseMoveByEmulator += (s, e) => this.OnMouseMoveByEmulator(e);
            VirtualMachine.MouseMove += (s, e) => this.OnMouseMove(e);
            VirtualMachine.MouseVisibilityChanged += (s, e) => this.OnMouseVisibilityChanged(e);
            VirtualMachine.CursorVisibilityChanged += (s, e) => this.OnCursorVisibilityChanged(e);
            VirtualMachine.CurrentProcessChanged += (s, e) => this.OnCurrentProcessChanged(e);

            this.log = instructionLog;
        }

        /// <summary>
        /// Occurs when the emulated display mode has changed.
        /// </summary>
        public event EventHandler VideoModeChanged;
        /// <summary>
        /// Occurs when the emulator sets the mouse position.
        /// </summary>
        public event EventHandler<MouseMoveEventArgs> MouseMoveByEmulator;
        /// <summary>
        /// Occurs when the internal mouse position has changed.
        /// </summary>
        public event EventHandler<MouseMoveEventArgs> MouseMove;
        /// <summary>
        /// Occurs when the mouse cursor is shown or hidden.
        /// </summary>
        public event EventHandler MouseVisibilityChanged;
        /// <summary>
        /// Occurs when the text-mode cursor is shown or hidden.
        /// </summary>
        public event EventHandler CursorVisibilityChanged;
        /// <summary>
        /// Occurs when the current process has changed.
        /// </summary>
        public event EventHandler CurrentProcessChanged;
        /// <summary>
        /// Occurs when the emulator state has changed.
        /// </summary>
        public event EventHandler StateChanged;
        /// <summary>
        /// Occurs when the emulator has halted due to an error.
        /// </summary>
        public event EventHandler<ErrorEventArgs> Error;

        /// <summary>
        /// Gets the current state of the emulated system.
        /// </summary>
        public EmulatorState State
        {
            get => currentState;
            private set
            {
                if (value != currentState)
                {
                    currentState = value;
                    ThreadPool.QueueUserWorkItem(state => OnStateChanged(EventArgs.Empty));
                }
            }
        }
        /// <summary>
        /// Gets the hosted VirtualMachine instance.
        /// </summary>
        public VirtualMachine VirtualMachine { get; }
        /// <summary>
        /// Gets the total number of instructions executed.
        /// </summary>
        public long TotalInstructions => totalInstructions;
        /// <summary>
        /// Gets or sets the current emulation speed.
        /// </summary>
        public int EmulationSpeed
        {
            get => this.emulationSpeed;
            set
            {
                if (value < MinimumSpeed)
                    throw new ArgumentOutOfRangeException();

                this.emulationSpeed = value;
            }
        }
        /// <summary>
        /// Gets or sets the object to use for raising events.
        /// </summary>
        public IEventSynchronizer EventSynchronizer { get; set; }

        /// <summary>
        /// Loads an executable program image into the emulator.
        /// </summary>
        /// <param name="fileName">Name of executable file to load.</param>
        public void LoadProgram(string fileName) => this.LoadProgram(fileName, null);
        /// <summary>
        /// Loads an executable program image into the emulator.
        /// </summary>
        /// <param name="fileName">Name of executable file to load.</param>
        /// <param name="commandLineArguments">Command line arguments for the program.</param>
        public void LoadProgram(string fileName, string commandLineArguments)
        {
            if (fileName == null)
                throw new ArgumentNullException(nameof(fileName));
            if (commandLineArguments != null && commandLineArguments.Length > 127)
                throw new ArgumentException("Command line length must not exceed 127 characters.");

            this.VirtualMachine.EndInitialization();

            var image = ProgramImage.Load(fileName, VirtualMachine);
            this.VirtualMachine.LoadImage(image, commandLineArguments);

            // Sierra adventure games assume a virtual width of 320 for the mouse for some reason.
            string upperFileName = fileName.ToUpper();
            if (upperFileName.Contains("SIERRA.EXE") || upperFileName.Contains("SCIDHUV.EXE"))
                VirtualMachine.Mouse.UseDefaultVirtualWidth = false;
            else
                VirtualMachine.Mouse.UseDefaultVirtualWidth = true;

            this.State = EmulatorState.Ready;
        }
        /// <summary>
        /// Starts or resumes emulation.
        /// </summary>
        public void Run()
        {
            if (this.State == EmulatorState.NoProgram)
                throw new InvalidOperationException("No program has been loaded.");
            if (this.State == EmulatorState.ProgramExited)
                throw new InvalidOperationException("The program has completed.");

            if (this.State == EmulatorState.Ready)
            {
                this.processorThread = new Thread(this.ProcessorThreadMain);
                this.processorThread.IsBackground = true;
                this.processorThread.Start();
            }
            else if (this.State == EmulatorState.Paused)
            {
                this.resumeEvent.Set();
            }
        }
        /// <summary>
        /// Pauses emulation.
        /// </summary>
        public void Pause()
        {
            if (this.State != EmulatorState.Running)
                throw new InvalidOperationException("No program is running.");

            this.targetState = EmulatorState.Paused;
        }
        /// <summary>
        /// Immediately stops emulation and places the emulator in a halted state.
        /// </summary>
        public void Halt()
        {
            bool paused = this.State == EmulatorState.Paused;

            this.State = EmulatorState.Halted;
            this.targetState = EmulatorState.Halted;

            if (paused)
                this.resumeEvent.Set();
        }
        /// <summary>
        /// Presses a key on the emulated keyboard.
        /// </summary>
        /// <param name="key">Key to press.</param>
        public void PressKey(Keys key)
        {
            if (this.keysPresssed.Add(key))
                this.VirtualMachine.PressKey(key);
        }
        /// <summary>
        /// Releases a key on the emulated keyboard.
        /// </summary>
        /// <param name="key">Key to release.</param>
        public void ReleaseKey(Keys key)
        {
            if (this.keysPresssed.Remove(key))
                this.VirtualMachine.ReleaseKey(key);
        }
        /// <summary>
        /// Releases all currently pressed keyboard keys.
        /// </summary>
        public void ReleaseAllKeys()
        {
            var pressedKeys = this.keysPresssed.ToList();
            foreach (var key in pressedKeys)
                this.ReleaseKey(key);
        }
        /// <summary>
        /// Signals that a mouse input event has occurred.
        /// </summary>
        /// <param name="mouseEvent">Mouse input event that has occurred.</param>
        public void MouseEvent(MouseEvent mouseEvent)
        {
            if (mouseEvent == null)
                throw new ArgumentNullException(nameof(mouseEvent));

            this.mouseQueue.Enqueue(mouseEvent);
        }
        /// <summary>
        /// Releases resources used by the emulator.
        /// </summary>
        public void Dispose() => this.Dispose(true);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void EmulateInstructions(int count)
        {
            var vm = this.VirtualMachine;
            vm.PerformDmaTransfers();

            var p = vm.Processor;

            try
            {
                if (p.Flags.InterruptEnable & !p.TemporaryInterruptMask)
                {
                    while (p.InPrefix)
                        vm.Emulate();

                    this.CheckHardwareInterrupts();
                }

                vm.Emulate(count);
            }
            catch (EmulatedException ex)
            {
                if (!vm.RaiseException(ex))
                    throw;
            }
            catch (EnableInstructionTrapException)
            {
                vm.Emulate();
                while (p.InPrefix)
                    vm.Emulate();

                if (p.Flags.Trap)
                {
                    vm.RaiseInterrupt(1);
                    p.Flags.Trap = false;
                }
            }
        }
        private void EmulateInstructionsWithLogging(int count)
        {
            var vm = this.VirtualMachine;
            vm.PerformDmaTransfers();

            try
            {
                if (vm.Processor.Flags.InterruptEnable)
                {
                    while (vm.Processor.InPrefix)
                        vm.Emulate(this.log);

                    this.CheckHardwareInterrupts();
                }

                for (int i = 0; i < count; i++)
                    vm.Emulate(this.log);
            }
            catch (EmulatedException ex)
            {
                if (!vm.RaiseException(ex))
                    throw;
            }
            catch (EnableInstructionTrapException)
            {
                vm.Emulate(this.log);
                while (vm.Processor.InPrefix)
                    vm.Emulate(this.log);

                if (vm.Processor.Flags.Trap)
                {
                    vm.RaiseInterrupt(1);
                    vm.Processor.Flags.Trap = false;
                }
            }
        }
        private void CheckHardwareInterrupts()
        {
            if (!this.RaiseMouseEvent())
            {
                var vm = this.VirtualMachine;

                if (!vm.Keyboard.IsHardwareQueueEmpty)
                    vm.InterruptController.RaiseHardwareInterrupt(1);

                int irq = vm.InterruptController.AcknowledgeRequest();
                if (irq >= 0)
                {
                    vm.RaiseInterrupt((byte)irq);

                    if (irq == vm.InterruptController.BaseInterruptVector1 + 1)
                        vm.PrepareForKeyboardHandler();
                }
            }
        }
        private void OnVideoModeChanged(EventArgs e)
        {
            var videoModeChanged = this.VideoModeChanged;
            if (videoModeChanged != null)
                this.RaiseEvent(videoModeChanged, e);
        }
        private void OnMouseMoveByEmulator(MouseMoveEventArgs e)
        {
            var mouseMove = this.MouseMoveByEmulator;
            if (mouseMove != null)
                this.RaiseEvent(mouseMove, e);
        }
        private void OnMouseMove(MouseMoveEventArgs e)
        {
            var mouseMove = this.MouseMove;
            if (mouseMove != null)
                this.RaiseEvent(mouseMove, e);
        }
        private void OnMouseVisibilityChanged(EventArgs e)
        {
            var visChanged = this.MouseVisibilityChanged;
            if (visChanged != null)
                this.RaiseEvent(visChanged, e);
        }
        private void OnCursorVisibilityChanged(EventArgs e)
        {
            var visChanged = this.CursorVisibilityChanged;
            if (visChanged != null)
                this.RaiseEvent(visChanged, e);
        }
        private void OnCurrentProcessChanged(EventArgs e)
        {
            var processChanged = this.CurrentProcessChanged;
            if (processChanged != null)
                this.RaiseEvent(processChanged, e);
        }
        private void OnStateChanged(EventArgs e)
        {
            var stateChanged = this.StateChanged;
            if (stateChanged != null)
                this.RaiseEvent(stateChanged, e);
        }
        private void OnError(ErrorEventArgs e)
        {
            var error = this.Error;
            if (error != null)
                this.RaiseEvent(error, e);
        }
        private void RaiseEvent(Delegate method, EventArgs e)
        {
            var sync = this.EventSynchronizer;
            if (sync == null)
                method.DynamicInvoke(this, e);
            else
                sync.BeginInvoke(method, this, e);
        }
        private void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                this.Halt();
                this.processorThread?.Join();
                this.resumeEvent?.Close();
                this.VirtualMachine?.Dispose();
                this.disposed = true;
            }
        }
        private void ProcessorThreadMain()
        {
            while (this.targetState != EmulatorState.Halted)
            {
                try
                {
                    this.State = EmulatorState.Running;
                    this.EmulationLoop();
                }
                catch (EndOfProgramException)
                {
                    this.State = EmulatorState.ProgramExited;
                    return;
                }
                catch (NotImplementedException ex)
                {
                    this.State = EmulatorState.Halted;
                    this.OnError(new ErrorEventArgs(ex.Message));
                    return;
                }
                catch (NotSupportedException)
                {
                    this.State = EmulatorState.Halted;
                    this.OnError(new ErrorEventArgs("Unknown error."));
                    return;
                }
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void EmulationLoop()
        {
            const int InstructionBatchCount = 500;
            const int Iterations = 5;
            const int IterationInstructionCount = InstructionBatchCount * Iterations;

            try
            {
                var speedTimer = new Stopwatch();

                bool breakPause = false;
                var vm = this.VirtualMachine;
                var interruptTimer = vm.InterruptTimer;
                interruptTimer.Reset();

                while (true)
                {
                    speedTimer.Restart();
                    for (int i = 0; i < Iterations; i++)
                    {
                        if (interruptTimer.IsIntervalComplete)
                        {
                            // Raise the hardware timer interrupt.
                            vm.InterruptController.RaiseHardwareInterrupt(0);
                            interruptTimer.Reset();
                        }

                        if (this.log == null)
                            this.EmulateInstructions(InstructionBatchCount);
                        else
                            this.EmulateInstructionsWithLogging(InstructionBatchCount);

                        Interlocked.Add(ref totalInstructions, InstructionBatchCount);

                        if (this.targetState == EmulatorState.Halted)
                            return;
                    }

                    long ticksPerInst = TimeSpan.TicksPerSecond / this.emulationSpeed;
                    long targetTicks = ticksPerInst * IterationInstructionCount;

                    while (speedTimer.ElapsedTicks < targetTicks)
                    {
                        Thread.SpinWait(100);
                    }

                    bool pauseSignal = this.targetState == EmulatorState.Paused;
                    if (pauseSignal || breakPause)
                    {
                        foreach (var device in vm.Devices)
                            device.Pause();

                        this.State = EmulatorState.Paused;
                        this.resumeEvent.WaitOne();

                        foreach (var device in vm.Devices)
                            device.Resume();

                        if (this.targetState == EmulatorState.Halted)
                            return;

                        breakPause = false;
                        this.targetState = EmulatorState.Running;
                        this.State = EmulatorState.Running;
                    }
                }
            }
            finally
            {
                this.log?.Dispose();
            }
        }
        private bool RaiseMouseEvent()
        {
            if (this.mouseQueue.TryDequeue(out var mouseEvent))
            {
                this.VirtualMachine.MouseEvent(mouseEvent);
                return true;
            }
            else
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Describes the current state of the emulated system.
    /// </summary>
    public enum EmulatorState
    {
        /// <summary>
        /// The emulator is initialized but no program has been loaded yet.
        /// </summary>
        NoProgram,
        /// <summary>
        /// The emulator is ready to run.
        /// </summary>
        Ready,
        /// <summary>
        /// The emulator is running.
        /// </summary>
        Running,
        /// <summary>
        /// The emulator is paused.
        /// </summary>
        Paused,
        /// <summary>
        /// The emulator has reached the end of the currently loaded program.
        /// </summary>
        ProgramExited,
        /// <summary>
        /// The emulator has been halted and cannot be resumed.
        /// </summary>
        Halted
    }

    /// <summary>
    /// Provides information about an error in emulation.
    /// </summary>
    public sealed class ErrorEventArgs : EventArgs
    {
        /// <summary>
        /// Initializes a new instance of the ErrorEventArgs class.
        /// </summary>
        /// <param name="message">Message describing the error.</param>
        public ErrorEventArgs(string message) => this.Message = message;

        /// <summary>
        /// Gets a message describing the error.
        /// </summary>
        public string Message { get; }
    }
}
